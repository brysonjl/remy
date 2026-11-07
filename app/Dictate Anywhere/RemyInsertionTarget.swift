import AppKit
import ApplicationServices
import os

/// Private, ephemeral destination authority. Never contains field text or titles.
/// Unknown/changed/secure targets allow recovery copying, never automatic paste.
nonisolated final class RemyInsertionTarget: @unchecked Sendable {
    static let legacyUnchecked = RemyInsertionTarget(terminal: false, check: { _ in true })
    static let unavailable = RemyInsertionTarget(terminal: false, check: { _ in false })
    private let restoration: (@MainActor @Sendable (RemyDeliveryAuthorization) async -> Bool)?
    private let terminal: Bool
    private let check: @Sendable (pid_t?) -> Bool
    private static let logger = Logger(subsystem: RemyDiagnostics.subsystem, category: "InsertionTarget")
    private static func trace(_ reason: StaticString, attribute: String = "none", status: Int32 = 0) {
        let fixedReason = String(describing: reason)
        logger.info("target reason=\(fixedReason, privacy: .public) attribute=\(attribute, privacy: .public) status=\(status)")
    }
    private static func roleClass(_ role: String?) -> String {
        let allowed = ["AXTextField", "AXTextArea", "AXComboBox", "AXSecureTextField", "AXWindow", "AXGroup", "AXScrollArea", "AXWebArea", "AXButton", "AXStaticText", "AXApplication"]
        return role.map { allowed.contains($0) ? $0 : "other" } ?? "missing"
    }
    private static func logFacts(_ reason: StaticString, facts: Editability, selectedTextSettable: Int = -1) {
        func flag(_ value: Bool?) -> Int { value.map { $0 ? 1 : 0 } ?? -1 }
        let fixedReason = String(describing: reason)
        logger.info("target reason=\(fixedReason, privacy: .public) role=\(roleClass(facts.role), privacy: .public) secure=\(facts.subrole == "AXSecureTextField") enabled=\(flag(facts.enabled)) editable=\(flag(facts.editable)) readonly=\(flag(facts.readOnly)) protected=\(flag(facts.protected)) valueSettable=\(facts.valueSettable) selectedTextSettable=\(selectedTextSettable)")
    }
    private static let eventQueue = DispatchQueue(label: "dev.remy.target-event", qos: .userInitiated)

    /// Internal fake seam: no AX access, clipboard or system events required.
    init(terminal: Bool, check: @escaping @Sendable (pid_t?) -> Bool,
         restoration: (@MainActor @Sendable (RemyDeliveryAuthorization) async -> Bool)? = nil) {
        self.terminal = terminal; self.check = check; self.restoration = restoration
    }
    @MainActor
    func restoreFocus(authorization: RemyDeliveryAuthorization) async -> Bool {
        guard !Task.isCancelled, authorization.isEligible, let restoration else { return false }
        let restored = await restoration(authorization)
        return restored && !Task.isCancelled && authorization.isEligible
    }
    func permitsPaste(processIdentifier: pid_t?, payload: String) -> Bool {
        guard !(terminal && payload.contains(where: \.isNewline)) else { Self.trace("terminalNewline"); return false }
        return check(processIdentifier)
    }
    /// Keyboard fallback performs validation and its balanced dispatch on a worker.
    func performOnWorker<T: Sendable>(_ action: @escaping @Sendable () -> T) async -> T {
        await withCheckedContinuation { continuation in
            Self.eventQueue.async { continuation.resume(returning: action()) }
        }
    }

    static func capture(processIdentifier: pid_t, launchDate: Date?) -> RemyInsertionTarget {
        trace("captureBegin")
        guard let launchDate, let running = NSRunningApplication(processIdentifier: processIdentifier),
              running.isActive, !running.isTerminated, running.launchDate == launchDate else { trace("captureProcess"); return .unavailable }
        let deadline = ContinuousClock.now + .milliseconds(200)
        let app = AXUIElementCreateApplication(processIdentifier)
        _ = AXUIElementSetMessagingTimeout(app, 0.025)
        guard let window = element(kAXFocusedWindowAttribute, from: app, deadline: deadline),
              let field = element(kAXFocusedUIElementAttribute, from: app, deadline: deadline),
              belongs(window, to: processIdentifier, deadline: deadline), belongs(field, to: processIdentifier, deadline: deadline),
              editable(field, window: window, terminal: isTerminal(running.bundleIdentifier), deadline: deadline),
              finalIdentityMatches(pid: processIdentifier, launchDate: launchDate, app: app,
                  window: window, field: field, deadline: deadline) else { trace("captureRejected"); return .unavailable }
        trace("captureSuccess")
        let proof = Proof(pid: processIdentifier, launchDate: launchDate, app: app, window: window, field: field, terminal: isTerminal(running.bundleIdentifier))
        return RemyInsertionTarget(terminal: isTerminal(running.bundleIdentifier), check: {
            proof.matches($0)
        }, restoration: { authorization in
            await proof.restore(authorization)
        })
    }
    /// AX references are immutable and used only on bounded IPC workers.
    private final class Proof: @unchecked Sendable {
        let pid: pid_t
        let launchDate: Date
        let app: AXUIElement
        let window: AXUIElement
        let field: AXUIElement
        let terminal: Bool
        init(pid: pid_t, launchDate: Date, app: AXUIElement, window: AXUIElement, field: AXUIElement, terminal: Bool) {
            self.pid = pid; self.launchDate = launchDate; self.app = app; self.window = window; self.field = field; self.terminal = terminal
        }
        private func originalIsEligible(deadline: ContinuousClock.Instant) -> Bool {
            guard ContinuousClock.now < deadline,
                  let running = NSRunningApplication(processIdentifier: pid),
                  !running.isTerminated, running.launchDate == launchDate,
                  belongs(window, to: pid, deadline: deadline), belongs(field, to: pid, deadline: deadline),
                  editable(field, window: window, terminal: terminal, deadline: deadline) else { return false }
            return true
        }

        @MainActor
        func restore(_ authorization: RemyDeliveryAuthorization) async -> Bool {
            let overallDeadline = ContinuousClock.now + .milliseconds(800)
            let initial = await RemyInsertionTarget.unavailable.performOnWorker { [self] in
                guard authorization.isEligible, ContinuousClock.now < overallDeadline else { return 0 }
                if matches(pid) { return 1 }
                return originalIsEligible(deadline: min(overallDeadline, ContinuousClock.now + .milliseconds(200))) ? 2 : 0
            }
            guard initial != 0, authorization.isEligible, !Task.isCancelled, ContinuousClock.now < overallDeadline else { return false }
            if initial == 1 { return true } // Already exact: no activation/focus effect.
            guard let running = NSRunningApplication(processIdentifier: pid),
                  !running.isTerminated, running.launchDate == launchDate else { return false }
            if !running.isActive {
                guard authorization.performFocusMutation({
                    guard !running.isTerminated, running.launchDate == launchDate,
                          ContinuousClock.now < overallDeadline else { return false }
                    return running.activate()
                }) else { return false }
            }
            let activationDeadline = min(overallDeadline, ContinuousClock.now + .milliseconds(120))
            while !running.isActive {
                guard authorization.isEligible, !Task.isCancelled, ContinuousClock.now < activationDeadline else { return false }
                try? await Task.sleep(for: .milliseconds(8))
            }
            guard authorization.isEligible, !Task.isCancelled else { return false }
            return await RemyInsertionTarget.unavailable.performOnWorker { [self] in
                let deadline = min(overallDeadline, ContinuousClock.now + .milliseconds(200))
                guard authorization.isEligible, originalIsEligible(deadline: deadline),
                      running.isActive, !running.isTerminated, running.launchDate == launchDate else { return false }
                if let focused = element(kAXFocusedWindowAttribute, from: app, deadline: deadline), !CFEqual(window, focused) {
                    guard ContinuousClock.now < deadline, authorization.performFocusMutation({
                        guard running.isActive, !running.isTerminated, running.launchDate == launchDate,
                              ContinuousClock.now < deadline else { return false }
                        return AXUIElementPerformAction(window, kAXRaiseAction as CFString) == .success
                    }) else { return false }
                }
                guard ContinuousClock.now < deadline, authorization.isEligible else { return false }
                if element(kAXFocusedWindowAttribute, from: app, deadline: deadline).map({ CFEqual(window, $0) }) != true {
                    guard ContinuousClock.now < deadline, authorization.performFocusMutation({
                        guard running.isActive, !running.isTerminated, running.launchDate == launchDate,
                              ContinuousClock.now < deadline else { return false }
                        return AXUIElementSetAttributeValue(app, kAXFocusedWindowAttribute as CFString, window) == .success
                    }) else { return false }
                }
                guard ContinuousClock.now < deadline, authorization.isEligible,
                      element(kAXFocusedWindowAttribute, from: app, deadline: deadline).map({ CFEqual(window, $0) }) == true else { return false }
                if element(kAXFocusedUIElementAttribute, from: app, deadline: deadline).map({ CFEqual(field, $0) }) != true {
                    guard ContinuousClock.now < deadline, authorization.performFocusMutation({
                        guard running.isActive, !running.isTerminated, running.launchDate == launchDate,
                              ContinuousClock.now < deadline else { return false }
                        return AXUIElementSetAttributeValue(field, kAXFocusedAttribute as CFString, kCFBooleanTrue) == .success
                    }) else { return false }
                }
                guard authorization.isEligible, editable(field, window: window, terminal: terminal, deadline: deadline) else { return false }
                return finalIdentityMatches(pid: pid, launchDate: launchDate, app: app,
                    window: window, field: field, deadline: deadline)
            }
        }
        func matches(_ requestedPID: pid_t?) -> Bool {
            trace("matchBegin")
            let deadline = ContinuousClock.now + .milliseconds(200)
            guard requestedPID == pid else { trace("matchRequestedPID"); return false }
            guard let running = NSRunningApplication(processIdentifier: pid),
                  !running.isTerminated, running.isActive, running.launchDate == launchDate else { trace("matchProcess"); return false }
            guard let currentWindow = element(kAXFocusedWindowAttribute, from: app, deadline: deadline), CFEqual(window, currentWindow) else { trace("matchWindow"); return false }
            guard let currentField = element(kAXFocusedUIElementAttribute, from: app, deadline: deadline), CFEqual(field, currentField) else { trace("matchField"); return false }
            guard belongs(currentField, to: pid, deadline: deadline), editable(currentField, window: currentWindow, terminal: terminal, deadline: deadline),
                  finalIdentityMatches(pid: pid, launchDate: launchDate, app: app,
                      window: window, field: field, deadline: deadline) else { trace("matchRejected"); return false }
            trace("matchSuccess")
            return true
        }
    }
    /// Repeat after the ancestry walk; that walk must not bless stale focus.
    private static func finalIdentityMatches(pid: pid_t, launchDate: Date, app: AXUIElement,
                                             window: AXUIElement, field: AXUIElement,
                                             deadline: ContinuousClock.Instant) -> Bool {
        guard ContinuousClock.now < deadline,
              let running = NSRunningApplication(processIdentifier: pid), running.isActive,
              !running.isTerminated, running.launchDate == launchDate,
              let lastWindow = element(kAXFocusedWindowAttribute, from: app, deadline: deadline), CFEqual(window, lastWindow),
              let lastField = element(kAXFocusedUIElementAttribute, from: app, deadline: deadline), CFEqual(field, lastField),
              ContinuousClock.now < deadline else { trace("finalIdentity"); return false }
        return true
    }
    private static func belongs(_ element: AXUIElement, to pid: pid_t, deadline: ContinuousClock.Instant) -> Bool {
        guard ContinuousClock.now < deadline else { trace("pidDeadline"); return false }
        var actual: pid_t = 0
        let status = AXUIElementGetPid(element, &actual)
        guard status == .success, actual == pid else { trace("elementPID", status: status.rawValue); return false }
        return true
    }
    private static func element(_ attribute: String, from source: AXUIElement, deadline: ContinuousClock.Instant) -> AXUIElement? {
        guard ContinuousClock.now < deadline else { trace("elementDeadline", attribute: attribute); return nil }
        var value: CFTypeRef?
        let status = AXUIElementCopyAttributeValue(source, attribute as CFString, &value)
        guard status == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { trace("elementMissingOrMalformed", attribute: attribute, status: status.rawValue); return nil }
        let element = unsafeBitCast(value, to: AXUIElement.self)
        _ = AXUIElementSetMessagingTimeout(element, 0.025)
        return element
    }
    struct Editability: Sendable {
        var role: String?
        var subrole: String? = nil
        var enabled: Bool? = nil
        var editable: Bool? = nil
        var readOnly: Bool? = nil
        var protected: Bool? = nil
        var valueSettable = false
    }
    static func supports(_ facts: Editability, terminal: Bool) -> Bool {
        // AXEnabled is optional on native text views (including TextEdit).
        // An explicit false still denies via unsafe; absence must not override
        // an affirmative writing capability or a known terminal text surface.
        guard !unsafe(facts),
              ["AXTextField", "AXTextArea", "AXComboBox"].contains(facts.role ?? ""),
              facts.editable != false else { return false }
        if terminal { return facts.role == "AXTextField" || facts.role == "AXTextArea" }
        return facts.editable == true || facts.valueSettable
    }
    private static func unsafe(_ facts: Editability) -> Bool {
        facts.role == "AXSecureTextField" || facts.subrole == "AXSecureTextField"
            || facts.readOnly == true || facts.protected == true || facts.enabled == false
    }
    private static func isTerminal(_ bundle: String?) -> Bool {
        ["com.mitchellh.ghostty", "com.apple.Terminal", "com.googlecode.iterm2"].contains(bundle ?? "")
    }

    // Web editors can have many structural AX ancestors between their focused
    // input and the browser window. Bound hops as well as IPC time, while still
    // inspecting every ancestor rather than skipping directly to AXWindow.
    static let maximumAncestryDepth = 128
    static func ancestorDepth<Element>(
        from field: Element,
        to window: Element,
        parent: (Element) -> Element?,
        isSafe: (Element) -> Bool,
        equals: (Element, Element) -> Bool,
        withinBudget: () -> Bool
    ) -> Int? {
        var current = field
        var visited = [field]
        for depth in 0...maximumAncestryDepth {
            guard withinBudget() else { trace("ancestryDeadline"); return nil }
            if equals(current, window) { return depth }
            guard depth < maximumAncestryDepth else { trace("ancestryDepthLimit"); return nil }
            guard let next = parent(current) else { trace("ancestorMissingOrCycle"); return nil }
            guard !visited.contains(where: { equals($0, next) }) else {
                trace("ancestorMissingOrCycle"); return nil
            }
            guard withinBudget() else { trace("ancestryDeadline"); return nil }
            guard isSafe(next) else { return nil }
            visited.append(next)
            current = next
        }
        return nil
    }

    private static func editable(_ field: AXUIElement, window: AXUIElement, terminal: Bool,
                                 deadline: ContinuousClock.Instant) -> Bool {
        guard let fieldFacts = try? facts(field, deadline: deadline) else { trace("fieldFacts"); return false }
        guard supports(fieldFacts, terminal: terminal) else {
            // Diagnostic only, on a rejected proof and only inside its budget.
            var selected = DarwinBoolean(false)
            var diagnostic = -1
            if ContinuousClock.now < deadline {
                let status = AXUIElementIsAttributeSettable(field, kAXSelectedTextAttribute as CFString, &selected)
                diagnostic = status == .success ? (selected.boolValue ? 1 : 0) : -1
                trace("selectedTextProbe", attribute: kAXSelectedTextAttribute, status: status.rawValue)
            }
            logFacts("unsupportedField", facts: fieldFacts, selectedTextSettable: diagnostic)
            return false
        }
        guard let depth = ancestorDepth(from: field, to: window, parent: {
            element(kAXParentAttribute, from: $0, deadline: deadline)
        }, isSafe: {
            guard let parentFacts = try? facts($0, includeSettable: false, deadline: deadline) else {
                trace("ancestorFacts"); return false
            }
            guard !unsafe(parentFacts) else { logFacts("unsafeAncestor", facts: parentFacts); return false }
            return true
        }, equals: { CFEqual($0, $1) }, withinBudget: { ContinuousClock.now < deadline }) else { return false }
        logger.info("target reason=ancestrySuccess depth=\(depth)")
        return true
    }

    /// Optional unsupported/no-value attributes are normal for native/browser AX.
    /// IPC failure or malformed successful values cannot establish safe authority.
    static func acceptsOptionalAttribute(status: AXError, valuePresent: Bool, typeMatches: Bool) -> Bool {
        status == .attributeUnsupported || status == .noValue
            || (status == .success && valuePresent && typeMatches)
    }
    private enum ReadFailure: Error { case unavailable }
    private static func facts(_ source: AXUIElement, includeSettable: Bool = true,
                              deadline: ContinuousClock.Instant) throws -> Editability {
        func value(_ key: String, type: CFTypeID) throws -> CFTypeRef? {
            guard ContinuousClock.now < deadline else { trace("factsDeadline", attribute: key); throw ReadFailure.unavailable }
            var result: CFTypeRef?
            let status = AXUIElementCopyAttributeValue(source, key as CFString, &result)
            guard acceptsOptionalAttribute(status: status, valuePresent: result != nil,
                typeMatches: result.map { CFGetTypeID($0) == type } ?? false),
                ContinuousClock.now < deadline else { trace("factsReadRejected", attribute: key, status: status.rawValue); throw ReadFailure.unavailable }
            return status == .success ? result : nil
        }
        func string(_ key: String) throws -> String? {
            try value(key, type: CFStringGetTypeID()) as? String
        }
        func boolean(_ key: String) throws -> Bool? {
            guard let result = try value(key, type: CFBooleanGetTypeID()) else { return nil }
            return CFBooleanGetValue(unsafeBitCast(result, to: CFBoolean.self))
        }
        let role = try string(kAXRoleAttribute)
        let subrole = try string(kAXSubroleAttribute)
        let enabled = try boolean(kAXEnabledAttribute)
        let editable = try boolean("AXEditable")
        let readOnly = try boolean("AXReadOnly")
        let protected = try boolean("AXProtectedContent")
        var settable = DarwinBoolean(false)
        if includeSettable {
            guard ContinuousClock.now < deadline else { trace("settableDeadline"); throw ReadFailure.unavailable }
            let status = AXUIElementIsAttributeSettable(source, kAXValueAttribute as CFString, &settable)
            guard status == .success || status == .attributeUnsupported || status == .noValue,
                  ContinuousClock.now < deadline else { trace("settableRejected", attribute: kAXValueAttribute, status: status.rawValue); throw ReadFailure.unavailable }
        }
        return Editability(role: role, subrole: subrole, enabled: enabled, editable: editable,
            readOnly: readOnly, protected: protected, valueSettable: settable.boolValue)
    }

}

/// One successful own-copy generation. Content equality alone cannot authorize
/// paste after another app replaces the clipboard (even with identical text).
nonisolated struct RemyClipboardOwnership: Sendable {
    struct State: Sendable {
        let generation: Int
        let text: String?
        let stable: Bool
    }
    let generation: Int
    let payload: String
    let read: @Sendable () -> State
    func matches() -> Bool {
        let current = read()
        return current.stable && current.generation == generation
            && current.text.map { $0.utf8.elementsEqual(payload.utf8) } == true
    }
    static func system(generation: Int, payload: String) -> Self {
        Self(generation: generation, payload: payload) {
            let board = NSPasteboard.general
            let before = board.changeCount
            let text = board.string(forType: .string)
            return State(generation: before, text: text, stable: before == board.changeCount)
        }
    }
}
