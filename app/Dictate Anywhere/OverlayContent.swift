//
//  OverlayContent.swift
//  Dictate Anywhere
//
//  Pill-shaped overlay content for dictation states.
//

import SwiftUI

enum OverlayMetrics {
    static let footprintScale: CGFloat = 0.65
    static let typeScale: CGFloat = 0.78

    static func size(_ value: CGFloat) -> CGFloat {
        (value * footprintScale).rounded()
    }

    static func type(_ value: CGFloat) -> CGFloat {
        (value * typeScale * 10).rounded() / 10
    }
}

/// Overlay display states
enum OverlayState: Equatable {
    case listening
    case processing
    case success
    case copiedOnly
    case cleanupFallback(reason: String, delivery: CleanupFallbackDelivery)
    case preparingModel(name: String)
}

enum CleanupFallbackDelivery: Equatable {
    case inserted, copied, failed
}

/// The compact overlay has room for two short lines. Keep provider errors in History.
enum CleanupFallbackPresentation {
    static func title(reason: String) -> String {
        RemyCleanupFailure(rawValue: reason)?.overlayTitle ?? "AI cleanup failed"
    }

    static func detail(delivery: CleanupFallbackDelivery) -> String {
        switch delivery {
        case .inserted: "Raw transcript used"
        case .copied: "Raw transcript · Press ⌘V"
        case .failed: "Raw text in Remy"
        }
    }

    static func accessibilityHint(delivery: CleanupFallbackDelivery) -> String {
        delivery == .failed
            ? "Delivery failed. Open Remy to copy the transcript."
            : "See History for processing details."
    }
}

/// Observable model bridging OverlayWindow → SwiftUI
@Observable
final class OverlayModel {
    private(set) var overlayState: OverlayState = .listening
    private(set) var audioLevel: Float = 0
    private(set) var transcript: String = ""
    var isVisible: Bool = false
    var cancellationProgress: Double?
    private(set) var busyMessage: String?

    func showBusy() { busyMessage = "Already working on your dictation" }
    func resetBusy() { busyMessage = nil }

    /// Keep waveform updates independent of the transcript and pill layout.
    func updateListening(level: Float, transcript: String) {
        if audioLevel != level { audioLevel = level }
        if self.transcript != transcript { self.transcript = transcript }
    }

    func updateState(_ state: OverlayState) {
        resetBusy()
        if state != .listening {
            updateListening(level: 0, transcript: "")
        }
        if overlayState != state { overlayState = state }
    }
}

struct OverlayContent: View {
    let model: OverlayModel
    var settings: Settings = .shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack {
            Spacer()
            OverlayPill(model: model, settings: settings)
                .scaleEffect(model.isVisible ? 1 : 0.95, anchor: .bottom)
                .opacity(model.isVisible ? 1 : 0)
                .animation(reduceMotion ? nil : .spring(response: 0.35, dampingFraction: 0.8), value: model.isVisible)
        }
        .padding(.bottom, OverlaySize.shadowInset)
        .frame(width: settings.overlaySize.canvasSize.width, height: settings.overlaySize.canvasSize.height)
    }
}

/// Audio updates stay in the waveform; only transcript changes touch text layout.
struct ListeningOverlayContent: View {
    let model: OverlayModel
    let showTextPreview: Bool
    let textColor: Color
    var size: OverlaySize = .comfortable

    @Environment(\.colorScheme) private var colorScheme
    private var palette: OverlayPalette { OverlayPalette(scheme: colorScheme) }

    var body: some View {
        if showTextPreview {
            VStack(alignment: .leading, spacing: size.serviceHeaderSpacing) {
                HStack(spacing: 6) {
                    OverlayServiceMark(tint: palette.accent, size: size.serviceHeaderHeight)
                    Text("LISTENING")
                        .font(.system(size: size == .compact ? 8 : 9, weight: .bold, design: .monospaced))
                        .tracking(1)
                        .foregroundStyle(palette.secondaryText)
                    Spacer(minLength: 0)
                    Circle()
                        .fill(palette.warmSignal)
                        .frame(width: 4, height: 4)
                }
                .frame(height: size.serviceHeaderHeight)
                .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: size.textWaveformSpacing) {
                    OverlayTranscriptContent(model: model, size: size, textColor: textColor)
                    OverlayWaveformContent(model: model, height: size.waveformHeight)
                }
            }
            .padding(.horizontal, size.horizontalPadding)
            .padding(.vertical, size.verticalPadding)
        } else {
            HStack(spacing: 8) {
                OverlayServiceMark(tint: palette.accent, size: 16)
                OverlayWaveformContent(model: model, height: size.waveformHeight)
            }
            .padding(.horizontal, size.horizontalPadding)
        }
    }
}

/// A tiny monochrome waiter head supports the words; no decorative animation.
struct OverlayServiceMark: View {
    let tint: Color
    var size: CGFloat = 16

    var body: some View {
        Image("MenuBarIcon")
            .renderingMode(.template)
            .resizable()
            .scaledToFit()
            .foregroundStyle(tint)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

struct OverlayWaveformContent: View {
    let model: OverlayModel
    var height: CGFloat = 22

    var body: some View {
        WaveformView(audioLevel: model.audioLevel, height: height, isActive: model.isVisible)
    }
}

enum OverlayPreviewText {
    static let maximumCharacters = Int(320 * OverlayMetrics.footprintScale)

    static func trimmed(_ transcript: String, maximumCharacters: Int = maximumCharacters) -> String {
        guard let start = transcript.index(
            transcript.endIndex,
            offsetBy: -maximumCharacters,
            limitedBy: transcript.startIndex
        ), start != transcript.startIndex else { return transcript }
        return "…" + String(transcript[start...])
    }
}

// MARK: - Preview surface

struct CancellationProgressView: View {
    let progress: Double
    let tint: Color

    var body: some View {
        VStack(spacing: 5) {
            Text("Hold to cancel")
                .font(.system(size: OverlayMetrics.type(13), weight: .medium))
                .foregroundStyle(tint)
            ProgressView(value: progress)
                .tint(tint)
                .accessibilityLabel("Hold to cancel")
        }
        .padding(.horizontal, 16)
    }
}

struct OverlaySurfaceModifier: ViewModifier {
    let isCircular: Bool
    var cornerRadius: CGFloat = OverlayMetrics.size(22)
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    func body(content: Content) -> some View {
        if isCircular {
            decorated(content: content, shape: Circle())
        } else {
            decorated(
                content: content,
                shape: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            )
        }
    }

    @ViewBuilder
    private func decorated<S: Shape>(content: Content, shape: S) -> some View {
        let palette = OverlayPalette(scheme: colorScheme)
        let isDark = colorScheme == .dark
        content
            .background(palette.surface, in: shape)
            .clipShape(shape)
            // A faint top highlight gives the opaque surface some lift without glass.
            .overlay(
                shape.stroke(
                    LinearGradient(
                        colors: [palette.highlight, palette.border],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: contrast == .increased ? 2 : 1.25
                )
            )
            .overlay(shape.stroke(contrast == .increased ? palette.text : palette.border, lineWidth: contrast == .increased ? 2 : 0.75))
            .shadow(color: .black.opacity(isDark ? 0.32 : 0.10), radius: 1, x: 0, y: 1)
            .shadow(color: .black.opacity(isDark ? 0.34 : 0.14), radius: 8, x: 0, y: 4)
    }
}

struct ProcessingStatusView: View {
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var isAnimating = false

    private var ringSize: CGFloat {
        OverlayMetrics.size(24)
    }

    private var centerDotSize: CGFloat {
        OverlayMetrics.size(5)
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(tint.opacity(0.12), lineWidth: 1)

            Circle()
                .trim(from: 0.16, to: 0.82)
                .stroke(
                    tint.opacity(0.9),
                    style: StrokeStyle(lineWidth: 1.35, lineCap: .round)
                )
                .rotationEffect(.degrees(isAnimating ? 360 : 0))

            Circle()
                .fill(tint.opacity(0.9))
                .frame(width: centerDotSize, height: centerDotSize)
        }
        .frame(width: ringSize, height: ringSize)
        .accessibilityLabel("Processing dictation")
        .onAppear {
            guard !reduceMotion, !isAnimating else { return }
            withAnimation(.linear(duration: 1.15).repeatForever(autoreverses: false)) {
                isAnimating = true
            }
        }
        .onChange(of: reduceMotion) { _, enabled in
            if enabled { isAnimating = false }
        }
    }
}

struct SuccessStatusView: View {
    let tint: Color
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var isRevealed = false

    private var ringSize: CGFloat {
        OverlayMetrics.size(24)
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(tint.opacity(0.12), lineWidth: 1)

            Circle()
                .stroke(tint.opacity(0.42), lineWidth: 1)
                .scaleEffect(isRevealed ? 1.28 : 0.72)
                .opacity(isRevealed ? 0 : 0.8)

            Circle()
                .trim(from: 0, to: isRevealed ? 1 : 0.12)
                .stroke(
                    tint.opacity(0.9),
                    style: StrokeStyle(lineWidth: 1.4, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))

            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: OverlayMetrics.type(18)))
                .foregroundStyle(tint)
                .scaleEffect(isRevealed ? 1 : 0.72)
                .opacity(isRevealed ? 1 : 0)
        }
        .frame(width: ringSize, height: ringSize)
        .accessibilityLabel("Dictation inserted")
        .onAppear {
            isRevealed = false
            withAnimation(reduceMotion ? nil : .spring(response: 0.34, dampingFraction: 0.72)) {
                isRevealed = true
            }
        }
    }
}
