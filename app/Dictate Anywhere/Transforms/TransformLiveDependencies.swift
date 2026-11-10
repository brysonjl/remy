//
//  TransformLiveDependencies.swift
//  Dictate Anywhere
//
//  AppKit/AX glue for TransformRunner and its overlay feedback.
//

import AppKit

extension TransformRunner.Dependencies {
    static func live(settings: Settings, permissions: Permissions, textInserter: TextInserter,
                     profile: @escaping () -> CleanupProfile?,
                     credentials: (any CleanupProfileCredentialStore)?) -> Self {
        Self(
            route: {
                TransformProviderRoute.capture(settings: settings, profile: profile(), credentials: credentials)
            },
            readSelection: {
                guard permissions.checkAccessibilityForUse(promptIfNeeded: false) else {
                    return .rejected(.accessibilityRequired)
                }
                guard let app = NSWorkspace.shared.frontmostApplication, !app.isTerminated,
                      app.processIdentifier != ProcessInfo.processInfo.processIdentifier else {
                    return .rejected(.selectionUnreadable)
                }
                // Destination identity first, as dictation does; it reads no text.
                let target = await textInserter.captureTarget(for: app)
                let pid = app.processIdentifier
                let bundleIdentifier = app.bundleIdentifier
                let appName = app.localizedName ?? bundleIdentifier ?? "Unknown app"
                let rules = settings.dictationAppRules
                let read = await Task.detached(priority: .userInitiated) {
                    TransformSelectionReader.read(processIdentifier: pid, bundleIdentifier: bundleIdentifier,
                                                  appName: appName, rules: rules)
                }.value
                switch read {
                case .text(let text): return .selected(TransformSelection(text: text, processIdentifier: pid, target: target))
                case .empty: return .rejected(.noSelection)
                case .protected: return .rejected(.protectedField)
                case .unreadable: return .rejected(.selectionUnreadable)
                }
            },
            selectionStillMatches: { selection in
                let pid = selection.processIdentifier
                let current = await Task.detached(priority: .userInitiated) {
                    TransformSelectionReader.currentSelection(processIdentifier: pid)
                }.value
                return current == selection.text
            },
            frontmostProcessIdentifier: { NSWorkspace.shared.frontmostApplication?.processIdentifier },
            deliver: { text, selection, pasteAutomatically, authorization in
                // Same guarded path as dictation: verbatim clipboard payload,
                // target proof, terminal multiline copy-only, revocable lease.
                await textInserter.insertText(text, targetProcessIdentifier: selection.processIdentifier,
                    pasteAutomatically: pasteAutomatically, verbatimPayload: true,
                    targetSnapshot: selection.target, authorization: authorization)
            })
    }
}

extension OverlayWindow {
    func present(_ event: TransformEvent) {
        switch event {
        case .processing(let name, let processIdentifier):
            beginSession(targetProcessIdentifier: processIdentifier)
            show(state: .transforming(name: name))
        case .finished(.inserted):
            show(state: .success)
            hide(afterDelay: 1.0)
        case .finished(.copied(nil)):
            show(state: .copiedOnly)
            hide(afterDelay: 1.5)
        case .finished(.cancelled):
            hide(afterDelay: 0)
        case .finished(.notStarted(let notice)) where notice == .alreadyRunning || notice == .dictationBusy:
            // Never replace the active session's own overlay state.
            showBusy(notice.title)
        case .finished(.copied(let notice?)), .finished(.notStarted(let notice)), .finished(.failed(let notice)):
            show(state: .transformNotice(title: notice.title, detail: notice.detail))
            hide(afterDelay: 4.0)
        }
    }
}
