//
//  TransformSelectionReader.swift
//  Dictate Anywhere
//
//  Reads the complete selected text of the focused field for a transform.
//  Dictation context capture bounds its selection snapshot to 600 characters,
//  so transforms read the selection directly, under the same exclusions.
//

import AppKit
import ApplicationServices
import os

nonisolated enum TransformSelectionReader {
    enum Read: Equatable {
        case text(String)
        case empty
        case protected
        case unreadable
    }

    private static let logger = Logger(subsystem: RemyDiagnostics.subsystem, category: "TransformSelection")

    /// Password managers, apps with context disabled by an app rule, and
    /// secure fields are never read. Blocks the calling thread for at most
    /// about 1.5 s while a Chromium/Electron app enables its accessibility tree.
    static func read(processIdentifier: pid_t, bundleIdentifier: String?, appName: String,
                     rules: [DictationAppRule]) -> Read {
        if isExcluded(bundleIdentifier: bundleIdentifier, appName: appName, rules: rules) { return .protected }
        let application = AXUIElementCreateApplication(processIdentifier)
        AXUIElementSetMessagingTimeout(application, 1.0)
        var element = DictationContextCapture.focusedTextElement(in: application, processIdentifier: processIdentifier)
        if element == nil, requestWebAccessibility(in: application) {
            let deadline = ProcessInfo.processInfo.systemUptime + 1.5
            repeat {
                Thread.sleep(forTimeInterval: 0.1)
                element = DictationContextCapture.focusedTextElement(
                    in: application, processIdentifier: processIdentifier, logFailure: false)
            } while element == nil && ProcessInfo.processInfo.systemUptime < deadline
        }
        guard let element else {
            logger.info("selection: no focused element")
            return .unreadable
        }
        return selectedText(in: element)
    }

    /// The live selection of the same app's focused element, or nil.
    static func currentSelection(processIdentifier: pid_t) -> String? {
        let application = AXUIElementCreateApplication(processIdentifier)
        AXUIElementSetMessagingTimeout(application, 0.5)
        guard let element = DictationContextCapture.focusedTextElement(
            in: application, processIdentifier: processIdentifier, logFailure: false) else { return nil }
        if case .text(let text) = selectedText(in: element) { return text }
        return nil
    }

    static func isExcluded(bundleIdentifier: String?, appName: String, rules: [DictationAppRule]) -> Bool {
        if DictationContextClassifier.isSensitiveApplication(bundleIdentifier: bundleIdentifier) { return true }
        return !DictationContextClassifier.classification(bundleIdentifier: bundleIdentifier, documentURL: nil,
            appName: appName, rules: rules).contextEnabled
    }

    private static func selectedText(in element: AXUIElement) -> Read {
        AXUIElementSetMessagingTimeout(element, 0.5)
        var subrole: CFTypeRef?
        let subroleStatus = AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &subrole)
        // A field that fails to report its subrole cannot prove it is not secure.
        guard subroleStatus == .success || subroleStatus == .attributeUnsupported || subroleStatus == .noValue,
              (subrole as? String) != (kAXSecureTextFieldSubrole as String) else { return .protected }

        var value: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXSelectedTextAttribute as CFString, &value) == .success,
           let text = value as? String {
            return text.isEmpty ? .empty : .text(text)
        }
        // Some editors expose only the selected range and a range reader.
        var rangeValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &rangeValue) == .success,
              let rangeValue, CFGetTypeID(rangeValue) == AXValueGetTypeID() else { return .unreadable }
        var range = CFRange()
        guard AXValueGetValue(rangeValue as! AXValue, .cfRange, &range) else { return .unreadable }
        guard range.length > 0 else { return .empty }
        guard let parameter = AXValueCreate(.cfRange, &range) else { return .unreadable }
        var text: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element,
                kAXStringForRangeParameterizedAttribute as CFString, parameter, &text) == .success,
              let string = text as? String else { return .unreadable }
        return string.isEmpty ? .empty : .text(string)
    }

    /// Same activation request as dictation context capture: Electron honors
    /// AXManualAccessibility, Chromium the legacy enhanced-interface flag.
    private static func requestWebAccessibility(in application: AXUIElement) -> Bool {
        if AXUIElementSetAttributeValue(application, "AXManualAccessibility" as CFString, kCFBooleanTrue) == .success {
            return true
        }
        let enhanced = AXUIElementSetAttributeValue(application, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue)
        return enhanced == .success || enhanced == .notImplemented
    }
}
