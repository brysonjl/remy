import AppKit
import Observation
import SwiftUI
import XCTest
@testable import Dictate_Anywhere

@MainActor
final class OverlayContentTests: XCTestCase {
    func testBusyFeedbackPreservesSessionDataAndResetsOnNextState() {
        let model = OverlayModel()
        model.updateState(.preparingModel(name: "Speech"))
        model.updateListening(level: 0.4, transcript: "Retained synthetic preview")
        model.cancellationProgress = 0.6
        model.showBusy()
        XCTAssertEqual(model.busyMessage, "Already working on your dictation")
        XCTAssertEqual(model.overlayState, .preparingModel(name: "Speech"))
        XCTAssertEqual(model.transcript, "Retained synthetic preview")
        XCTAssertEqual(model.audioLevel, 0.4)
        XCTAssertEqual(model.cancellationProgress, 0.6)
        model.updateState(.processing)
        XCTAssertNil(model.busyMessage)
        XCTAssertEqual(model.cancellationProgress, 0.6)
        model.showBusy()
        model.resetBusy()
        XCTAssertNil(model.busyMessage)
    }

    func testBusyNoticeInvalidatesMountedPillPresentationWithoutChangingState() {
        let model = OverlayModel()
        model.updateState(.processing)
        let pill = OverlayPill(model: model)
        let changes = observe { _ = pill.body }
        model.showBusy()
        XCTAssertEqual(changes.count, 1)
        XCTAssertEqual(model.overlayState, .processing)
    }

    func testPreviewPreservesEmptyAndCharacterLimitBoundaries() {
        let limit = OverlayPreviewText.maximumCharacters
        for length in [0, 1, limit - 1, limit, limit + 1] {
            let transcript = String(repeating: "a", count: length)
            let expected = length > limit ? "…" + String(repeating: "a", count: limit) : transcript
            XCTAssertEqual(OverlayPreviewText.trimmed(transcript), expected, "length=\(length)")
        }
    }

    func testPreviewKeepsCompleteUnicodeCharacters() {
        for pattern in ["Café déjà vu. ", "这是听写文本。", "👩🏽‍💻 👨‍👩‍👧‍👦 🇳🇱 e\u{301} "] {
            let transcript = String(repeating: pattern, count: 100)
            let expected = "…" + String(transcript.suffix(OverlayPreviewText.maximumCharacters))
            XCTAssertEqual(OverlayPreviewText.trimmed(transcript), expected)
        }
    }

    func testEveryReadingSizeBoundsLongUnicodePreviews() {
        let transcript = String(repeating: "👩🏽‍💻 这是听写文本 Café e\u{301} ", count: 1_000)
        for size in OverlaySize.allCases {
            let text = OverlayPreviewText.trimmed(transcript, maximumCharacters: size.characterLimit)
            XCTAssertEqual(text, "…" + String(transcript.suffix(size.characterLimit)))
        }
    }

    func testLevelOnlyUpdatesDoNotInvalidateTranscriptContent() {
        let model = OverlayModel()
        let transcript = String(repeating: "Long dictation preview. ", count: 1_000)
        model.updateListening(level: 0, transcript: transcript)
        let content = OverlayTranscriptContent(model: model, size: .comfortable, textColor: .white)
        let changes = observe { _ = content.body }

        for update in 0..<600 {
            model.updateListening(level: Float(update % 10) / 10, transcript: transcript)
        }

        XCTAssertEqual(changes.count, 0, "waveform updates must not invalidate the preview body")
        print("OVERLAY_OBSERVATION level_updates=600 transcript_invalidations=\(changes.count)")
        model.updateListening(level: 0.5, transcript: "A new transcript")
        XCTAssertEqual(changes.count, 1, "the preview must still observe transcript changes")
    }

    func testDisabledPreviewDoesNotObserveTranscript() {
        let model = OverlayModel()
        let content = ListeningOverlayContent(model: model, showTextPreview: false, textColor: .white)
        let changes = observe { _ = content.body }

        model.updateListening(level: 0.5, transcript: "Text that will not be displayed")

        XCTAssertEqual(changes.count, 0)
    }

    func testPreviewObservesCorrectionsClearingAndResumedText() {
        let model = OverlayModel()
        model.updateListening(level: 0.4, transcript: "first")
        let content = OverlayTranscriptContent(model: model, size: .comfortable, textColor: .white)
        for transcript in ["other", "", "resumed"] {
            let changes = observe { _ = content.body }
            model.updateListening(level: 0.4, transcript: transcript)
            XCTAssertEqual(changes.count, 1, "transcript=\(transcript)")
            XCTAssertEqual(model.transcript, transcript)
        }
    }

    func testWaveformObservesLevelWithoutTranscript() {
        let model = OverlayModel()
        model.updateListening(level: 0.4, transcript: "first")
        let waveform = OverlayWaveformContent(model: model)
        let changes = observe { _ = waveform.body }

        model.updateListening(level: 0.4, transcript: "corrected")
        XCTAssertEqual(changes.count, 0)
        model.updateListening(level: 0.6, transcript: "corrected")
        XCTAssertEqual(changes.count, 1)
    }

    func testListeningDataDoesNotInvalidatePillPresentation() {
        let model = OverlayModel()
        let changes = observe {
            _ = model.overlayState
            _ = model.isVisible
            _ = model.cancellationProgress
        }
        model.updateListening(level: 0.6, transcript: "hello")
        XCTAssertEqual(changes.count, 0)
        model.updateState(.processing)
        XCTAssertEqual(changes.count, 1)
    }

    func testStatusStatesReleaseListeningDataAndAllowANewSession() {
        let model = OverlayModel()
        let states: [OverlayState] = [.processing, .success, .copiedOnly,
                                     .cleanupFallback(reason: "timeout", delivery: .inserted),
                                     .cleanupFallback(reason: "authentication", delivery: .copied),
                                     .cleanupFallback(reason: "providerFailure", delivery: .failed),
                                     .preparingModel(name: "Speech")]
        for state in states {
            model.updateListening(level: 0.8, transcript: "Previous dictation")
            model.updateState(state)
            XCTAssertEqual(model.overlayState, state)
            XCTAssertEqual(model.audioLevel, 0)
            XCTAssertEqual(model.transcript, "")
        }
        model.updateListening(level: 0.2, transcript: "New session")
        model.updateState(.listening)
        XCTAssertEqual(model.overlayState, .listening)
        XCTAssertEqual(model.audioLevel, 0.2)
        XCTAssertEqual(model.transcript, "New session")
    }

    func testCleanupWarningNeverDisplaysAnUnrecognizedProviderError() {
        let providerError = "provider body with credential-like secret and endpoint details"
        XCTAssertEqual(CleanupFallbackPresentation.title(reason: providerError), "AI cleanup failed")
        XCTAssertEqual(CleanupFallbackPresentation.detail(delivery: .copied), "Raw transcript · Press ⌘V")
        XCTAssertEqual(CleanupFallbackPresentation.detail(delivery: .inserted), "Raw transcript used")
        XCTAssertEqual(CleanupFallbackPresentation.detail(delivery: .failed), "Raw text in Remy")
        XCTAssertEqual(CleanupFallbackPresentation.accessibilityHint(delivery: .failed),
                       "Delivery failed. Open Remy to copy the transcript.")
    }

    func testCleanupWarningLabelsFitWithoutTruncationInCompactOverlay() {
        // Compact warning width minus horizontal padding, warning icon and icon/text spacing.
        let availableWidth = OverlaySize.compact.width + 20 - 18 - 13 - 7
        let titleFont = NSFont.systemFont(ofSize: 11.5, weight: .semibold)
        let detailFont = NSFont.systemFont(ofSize: 10.5)
        for failure in RemyCleanupFailure.allCases {
            let title = CleanupFallbackPresentation.title(reason: failure.rawValue)
            let measured = (title as NSString).size(withAttributes: [.font: titleFont]).width
            XCTAssertLessThanOrEqual(measured, availableWidth, title)
        }
        for delivery in [CleanupFallbackDelivery.inserted, .copied, .failed] {
            let detail = CleanupFallbackPresentation.detail(delivery: delivery)
            let measured = (detail as NSString).size(withAttributes: [.font: detailFont]).width
            XCTAssertLessThanOrEqual(measured, availableWidth, detail)
        }
    }

    func testMountedOverlayPaintsListeningAndProcessingAcrossHideShowReuse() async throws {
        let settings = Settings.shared
        let oldPreview = settings.showTextPreview
        let oldSize = settings.overlaySize
        let defaults = UserDefaults.standard
        let savedDefaults = ["showTextPreview", "overlaySize"].map { ($0, defaults.object(forKey: $0)) }
        defer {
            settings.showTextPreview = oldPreview
            settings.overlaySize = oldSize
            // Restore absent preferences as absent, not as newly persisted defaults.
            for (key, value) in savedDefaults {
                if let value { defaults.set(value, forKey: key) }
                else { defaults.removeObject(forKey: key) }
            }
        }
        settings.overlaySize = .comfortable
        for scheme in [ColorScheme.light, .dark] {
            for preview in [false, true] {
                settings.showTextPreview = preview
                let model = OverlayModel()
                model.updateListening(level: 0.4, transcript: "Synthetic overlay visibility fixture.")
                let content = OverlayContent(model: model, settings: settings)
                    .transaction { transaction in
                        transaction.disablesAnimations = true
                        transaction.animation = nil
                    }
                    .environment(\.colorScheme, scheme)
                let host = NSHostingView(rootView: content)
                let size = settings.overlaySize.canvasSize
                let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.isReleasedWhenClosed = false
                window.isOpaque = false
                window.backgroundColor = .clear
                window.hasShadow = false
                host.frame = NSRect(origin: .zero, size: size)
                window.contentView = host
                window.orderFrontRegardless()
                defer { window.close() }
                let label = "\(scheme)-preview-\(preview)"
                try await assertMountedPaint(host, visible: false, label: label + "-initial-hidden")
                model.isVisible = true
                try await assertMountedPaint(host, visible: true, label: label + "-first-listening")
                model.updateState(.processing)
                try await assertMountedPaint(host, visible: true, label: label + "-first-processing")
                model.isVisible = false
                try await assertMountedPaint(host, visible: false, label: label + "-hidden")
                // Reuse the same model, hosting view, and native window.
                window.orderOut(nil)
                model.updateListening(level: 0.6, transcript: "Synthetic second recording.")
                model.updateState(.listening)
                model.isVisible = true
                window.orderFrontRegardless()
                try await assertMountedPaint(host, visible: true, label: label + "-reused-listening")
                model.updateState(.processing)
                try await assertMountedPaint(host, visible: true, label: label + "-reused-processing")
                model.updateState(.cleanupFallback(reason: "timeout", delivery: .copied))
                try await assertMountedPaint(host, visible: true, label: label + "-cleanup-fallback")
            }
        }
    }

    private func assertMountedPaint<V: View>(
        _ host: NSHostingView<V>, visible: Bool, label: String,
        file: StaticString = #filePath, line: UInt = #line
    ) async throws {
        // Yield to AppKit/SwiftUI until the observed model reaches the mounted
        // view. Disabled transactions avoid depending on spring-animation duration.
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        var lastPainted = 0
        var lastBitmap: NSBitmapImageRep?
        repeat {
            try await Task.sleep(for: .milliseconds(20))
            host.layoutSubtreeIfNeeded()
            host.display()
            let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds), file: file, line: line)
            XCTAssertTrue(bitmap.hasAlpha, "Visibility fixture needs an alpha-capable backing image", file: file, line: line)
            // Reused backing storage must not retain pixels from a previous state.
            if let bytes = bitmap.bitmapData {
                memset(bytes, 0, bitmap.bytesPerRow * bitmap.pixelsHigh)
            }
            host.cacheDisplay(in: host.bounds, to: bitmap)
            var painted = 0
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 2) {
                for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) {
                    if let color = bitmap.colorAt(x: x, y: y), color.alphaComponent > 0.5 {
                        painted += 1
                    }
                }
            }
            lastPainted = painted
            lastBitmap = bitmap
            // A status disk has hundreds of opaque pixels even at 1x. This
            // rejects a blank/transparent image rather than accepting nonnil PNG.
            if visible ? painted >= 50 : painted == 0 { break }
        } while ContinuousClock.now < deadline
        if visible {
            XCTAssertGreaterThanOrEqual(lastPainted, 50, "Mounted overlay was transparent: \(label)", file: file, line: line)
        } else {
            XCTAssertEqual(lastPainted, 0, "Hidden overlay still painted: \(label)", file: file, line: line)
        }
        // Optional artifacts contain only the synthetic fixture above. Bare
        // xcodebuild receives this via TEST_RUNNER_REMY_OVERLAY_RENDER_DIR.
        if let path = ProcessInfo.processInfo.environment["REMY_OVERLAY_RENDER_DIR"], !path.isEmpty,
           let bitmap = lastBitmap, let png = bitmap.representation(using: .png, properties: [:]) {
            let directory = URL(fileURLWithPath: path, isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try png.write(to: directory.appendingPathComponent(label + ".png"), options: .atomic)
        }
    }

    private func observe(_ read: () -> Void) -> OverlayObservationChanges {
        let changes = OverlayObservationChanges()
        withObservationTracking(read, onChange: { changes.record() })
        return changes
    }
}

nonisolated private final class OverlayObservationChanges: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func record() { lock.withLock { value += 1 } }
}
