//
//  ScreenNameCapture.swift
//  Dictate Anywhere
//
//  "Spell names from the active window": a bounded Accessibility walk of the
//  focused window, run after recording-start context capture. No screenshots
//  or OCR. Terms stay in memory for one dictation and are never stored/logged.
//

import AppKit
import Foundation

/// The one Accessibility read the walk needs, so caps and secure-field
/// handling can be tested against a fake tree.
nonisolated protocol ScreenNameNode {
    /// One batched read of subrole, text attributes and children.
    func read(messagingTimeout: Float) -> ScreenNameNodeSnapshot<Self>?
}

nonisolated struct ScreenNameNodeSnapshot<Node> {
    let subrole: String?
    /// String values of AXValue, AXTitle and AXDescription.
    let texts: [String]
    let children: [Node]
}

nonisolated enum ScreenNameWalk {
    struct Caps {
        var budget: Duration = .milliseconds(75)
        var maximumNodes = 400
        var maximumDepth = 40
        var messagingTimeout: Float = 0.05
        var maximumValueCharacters = 400
    }

    struct Result {
        var texts: [String] = []
        var nodes = 0
        var budgetHit = false
        var elapsed: Duration = .zero
    }

    /// Breadth-first so window headers, recipients and titles come before
    /// deep message bodies. Each read's timeout never exceeds what remains
    /// of the budget, so one slow element cannot stretch the walk.
    static func walk<Node: ScreenNameNode>(
        root: Node,
        caps: Caps = .init(),
        now: () -> ContinuousClock.Instant = { ContinuousClock.now },
        isCancelled: () -> Bool = { Task.isCancelled }
    ) -> Result {
        var result = Result()
        let start = now()
        let deadline = start + caps.budget
        var queue: [(node: Node, depth: Int)] = [(root, 0)]
        var head = 0
        var seen: Set<String> = []
        while head < queue.count, result.nodes < caps.maximumNodes, !isCancelled() {
            let remaining = now().duration(to: deadline)
            guard remaining > .zero else { result.budgetHit = true; break }
            let (node, depth) = queue[head]
            head += 1
            result.nodes += 1
            let seconds = Double(remaining.components.seconds) + Double(remaining.components.attoseconds) / 1e18
            guard let snapshot = node.read(messagingTimeout: min(caps.messagingTimeout, Float(seconds))) else { continue }
            // Never read a secure field or anything inside it.
            if snapshot.subrole == (kAXSecureTextFieldSubrole as String) { continue }
            for text in snapshot.texts {
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { continue }
                let bounded = String(trimmed.prefix(caps.maximumValueCharacters))
                if seen.insert(bounded).inserted { result.texts.append(bounded) }
            }
            if depth < caps.maximumDepth {
                queue.append(contentsOf: snapshot.children.map { ($0, depth + 1) })
            }
        }
        result.elapsed = start.duration(to: now())
        return result
    }
}

enum ScreenNamePolicy {
    /// Destinations whose window usually shows the people and identifiers a
    /// dictation refers to. Terminals, generic browsing and "other" are skipped.
    static let allowedCategories: Set<DictationContextCategory> = [
        .email, .workMessaging, .personalMessaging, .codingAgent, .aiChat,
    ]

    /// Cleanup modes that build a text prompt. S1-mini takes only a category.
    private static let promptCleanupModes: Set<TranscriptPostProcessingMode> = [
        .appleIntelligence, .ollama, .openRouter, .openAICompatible, .profile,
    ]

    /// Read anything only when this session can use what is read: Apple
    /// Speech contextual strings, or a cleanup prompt allowed to carry captured
    /// text (a remote provider only with the surrounding-text opt-in).
    static func hasConsumer(_ configuration: RemyDictationConfiguration) -> Bool {
        switch configuration.capture.engine {
        case .assemblyAI: return false
        case .appleSpeech: return true
        case .parakeet:
            let cleanup = configuration.cleanup
            return promptCleanupModes.contains(cleanup.mode) && cleanup.contextSupport.includesCapturedText(
                remoteSharingEnabled: cleanup.contextPolicy.shareCapturedTextRemotely)
        }
    }

    static func shouldCapture(_ configuration: RemyDictationConfiguration) -> Bool {
        RemyFeaturePolicy.allowsDictationContextGathering
            && configuration.capture.spellNamesFromActiveWindow
            && configuration.cleanup.contextPolicy.isEnabled
            && hasConsumer(configuration)
    }

    /// Classification already applied app rules and sensitive-app exclusions;
    /// both are rechecked here so the walk never depends on a caller.
    static func allowsWalk(in context: DictationContext) -> Bool {
        !context.isSecureField && !context.isContextExcluded
            && !DictationContextClassifier.isSensitiveApplication(bundleIdentifier: context.bundleIdentifier)
            && allowedCategories.contains(context.category)
    }
}

/// Live Accessibility element. One `AXUIElementCopyMultipleAttributeValues`
/// round trip per node. Never sets AXManualAccessibility or
/// AXEnhancedUserInterface: the walk reads only what the app already exposes.
nonisolated struct LiveScreenNameNode: ScreenNameNode {
    let element: AXUIElement

    private static let attributes: [CFString] = [
        kAXSubroleAttribute as CFString, kAXValueAttribute as CFString, kAXTitleAttribute as CFString,
        kAXDescriptionAttribute as CFString, kAXChildrenAttribute as CFString,
    ]

    func read(messagingTimeout: Float) -> ScreenNameNodeSnapshot<LiveScreenNameNode>? {
        AXUIElementSetMessagingTimeout(element, messagingTimeout)
        var values: CFArray?
        guard AXUIElementCopyMultipleAttributeValues(element, Self.attributes as CFArray,
                AXCopyMultipleAttributeOptions(rawValue: 0), &values) == .success,
              let array = values as? [Any], array.count == Self.attributes.count else { return nil }
        let children = (array[4] as? [AXUIElement] ?? []).map(LiveScreenNameNode.init(element:))
        return ScreenNameNodeSnapshot(subrole: array[0] as? String,
            texts: array[1...3].compactMap { $0 as? String }, children: children)
    }

    static func focusedWindow(processIdentifier: pid_t, messagingTimeout: Float = 0.05) -> LiveScreenNameNode? {
        let application = AXUIElementCreateApplication(processIdentifier)
        AXUIElementSetMessagingTimeout(application, messagingTimeout)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application, kAXFocusedWindowAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return LiveScreenNameNode(element: unsafeBitCast(value, to: AXUIElement.self))
    }
}

nonisolated enum ScreenNameCapture {
    /// Walks the target's focused window and extracts terms. Logs only counts.
    static func capture(processIdentifier: pid_t) -> [String] {
        let trace = PerfTrace.begin("dictation.screenNames")
        guard let root = LiveScreenNameNode.focusedWindow(processIdentifier: processIdentifier) else {
            trace.recordCounts(["nodes": 0, "terms": 0, "budget_hit": 0, "walk_ms": 0])
            trace.end(outcome: "noWindow")
            return []
        }
        return terms(walking: root, trace: trace)
    }

    static func terms<Node: ScreenNameNode>(walking root: Node, trace: PerfInterval) -> [String] {
        let walk = ScreenNameWalk.walk(root: root)
        let terms = ScreenNameTerms.extract(from: walk.texts)
        let milliseconds = Int(walk.elapsed.components.seconds * 1_000)
            + Int(walk.elapsed.components.attoseconds / 1_000_000_000_000_000)
        trace.recordCounts(["nodes": walk.nodes, "terms": terms.count,
            "budget_hit": walk.budgetHit ? 1 : 0, "walk_ms": milliseconds])
        trace.end(outcome: walk.budgetHit ? "budget" : (walk.nodes >= ScreenNameWalk.Caps().maximumNodes ? "nodeCap" : "completed"))
        return terms
    }
}
