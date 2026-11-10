import SwiftUI

/// Shared presentation used by both the floating panel and settings.
struct OverlayPill: View {
    let model: OverlayModel
    var settings: Settings = .shared
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Measured height of the live text block; animated so the pill grows a line at a time.
    @State private var listeningHeight: CGFloat?

    private var state: OverlayState { model.overlayState }

    private var showTextPreview: Bool {
        settings.showTextPreview
    }

    private var palette: OverlayPalette { OverlayPalette(scheme: colorScheme) }
    private var overlayTextColor: Color { palette.text }

    private var stateCategory: String {
        if model.busyMessage != nil { return "busy" }
        return switch state {
        case .listening: "listening"
        case .processing: "processing"
        case .success: "success"
        case .copiedOnly: "copiedOnly"
        case .cleanupFallback: "cleanupFallback"
        case .preparingModel: "preparingModel"
        case .transforming: "transforming"
        case .transformNotice: "transformNotice"
        }
    }

    private var statusCircleDiameter: CGFloat {
        OverlayMetrics.size(48)
    }

    private var isCircularStatusState: Bool {
        if model.cancellationProgress != nil || model.busyMessage != nil { return false }
        switch state {
        case .processing, .success:
            return true
        default:
            return false
        }
    }

    private var isTextPreview: Bool {
        model.cancellationProgress == nil && model.busyMessage == nil && state == .listening && showTextPreview
    }

    private var statusBottomInset: CGFloat {
        isCircularStatusState ? 1 : 0
    }

    private var pillWidth: CGFloat {
        if model.cancellationProgress != nil { return OverlayMetrics.size(260) }
        if model.busyMessage != nil { return min(settings.overlaySize.width, 260) }
        switch state {
        case .listening:
            return showTextPreview ? settings.overlaySize.width : settings.overlaySize.waveformOnlyWidth
        case .processing, .success:
            return statusCircleDiameter
        case .copiedOnly:
            return 144
        case .cleanupFallback, .transformNotice:
            return min(settings.overlaySize.width + 20, 300)
        case .preparingModel, .transforming:
            return min(settings.overlaySize.width, 240)
        }
    }

    private var pillHeight: CGFloat {
        if model.cancellationProgress != nil { return OverlayMetrics.size(64) }
        if model.busyMessage != nil { return 44 }
        switch state {
        case .listening:
            return showTextPreview
                ? listeningHeight ?? settings.overlaySize.minHeight
                : settings.overlaySize.waveformOnlyHeight
        case .processing, .success:
            return statusCircleDiameter
        case .copiedOnly:
            return 38
        case .cleanupFallback, .transformNotice:
            return 54
        case .preparingModel, .transforming:
            return 38
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            pillContent
                .id(stateCategory)
                .transition(reduceMotion ? .identity : .opacity)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.2), value: stateCategory)
        }
        .frame(width: pillWidth, height: pillHeight, alignment: isTextPreview ? .bottom : .center)
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.18), value: settings.overlaySize)
        .modifier(OverlaySurfaceModifier(
            isCircular: isCircularStatusState,
            cornerRadius: isTextPreview ? settings.overlaySize.cornerRadius : OverlayMetrics.size(22)
        ))
        .animation(reduceMotion ? nil : .spring(response: 0.42, dampingFraction: 0.82), value: isCircularStatusState)
        .padding(.bottom, statusBottomInset)
        .onChange(of: isTextPreview) { _, isTextPreview in
            if !isTextPreview { listeningHeight = nil }
        }
    }

    private func updateListeningHeight(_ height: CGFloat) {
        guard abs((listeningHeight ?? 0) - height) > 0.5 else { return }
        if listeningHeight == nil || reduceMotion {
            listeningHeight = height
        } else {
            withAnimation(.spring(response: 0.3, dampingFraction: 0.86)) { listeningHeight = height }
        }
    }

    @ViewBuilder
    private var pillContent: some View {
        if let progress = model.cancellationProgress {
            CancellationProgressView(progress: progress, tint: overlayTextColor)
        } else if let message = model.busyMessage {
            HStack(spacing: OverlayMetrics.size(10)) {
                ProcessingStatusView(tint: palette.accent)
                Text(message)
                    .font(DS.Fonts.display(12, .semibold))
                    .foregroundStyle(overlayTextColor)
                    .lineLimit(2)
            }
            .padding(.horizontal, OverlayMetrics.size(14))
            .accessibilityLabel(message)
        } else {
            switch state {
            case .listening:
                ListeningOverlayContent(
                    model: model,
                    showTextPreview: showTextPreview,
                    textColor: overlayTextColor,
                    size: settings.overlaySize
                )
                .fixedSize(horizontal: false, vertical: showTextPreview)
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height in
                    if showTextPreview { updateListeningHeight(height) }
                }

            case .processing:
                ProcessingStatusView(tint: palette.accent)

            case .success:
                SuccessStatusView(tint: palette.success)

            case .copiedOnly:
                HStack(spacing: OverlayMetrics.size(10)) {
                    Image(systemName: "doc.on.clipboard.fill")
                        .font(.system(size: OverlayMetrics.type(16)))
                        .foregroundStyle(palette.accent)
                    Text("Press ⌘V")
                        .font(DS.Fonts.display(12, .semibold))
                        .foregroundStyle(overlayTextColor.opacity(0.9))
                }

            case .cleanupFallback(let reason, let delivery):
                let title = CleanupFallbackPresentation.title(reason: reason)
                let detail = CleanupFallbackPresentation.detail(delivery: delivery)
                HStack(spacing: 7) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 13))
                        .foregroundStyle(palette.warmSignal)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(title)
                            .font(DS.Fonts.ui(11.5, .semibold))
                            .foregroundStyle(overlayTextColor)
                        Text(detail)
                            .font(DS.Fonts.ui(10.5))
                            .foregroundStyle(palette.secondaryText)
                    }
                    .lineLimit(1)
                }
                .padding(.horizontal, 9)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("\(title). \(detail). \(CleanupFallbackPresentation.accessibilityHint(delivery: delivery))")

            case .preparingModel(let name):
                HStack(spacing: OverlayMetrics.size(10)) {
                    ProcessingStatusView(tint: palette.accent)
                    Text("Loading \(name)…")
                        .font(DS.Fonts.display(12, .semibold))
                        .foregroundStyle(overlayTextColor.opacity(0.9))
                        .lineLimit(1)
                }
                .padding(.horizontal, OverlayMetrics.size(14))

            case .transforming(let name):
                HStack(spacing: OverlayMetrics.size(10)) {
                    ProcessingStatusView(tint: palette.accent)
                    Text("Transforming…")
                        .font(DS.Fonts.display(12, .semibold))
                        .foregroundStyle(overlayTextColor.opacity(0.9))
                        .lineLimit(1)
                }
                .padding(.horizontal, OverlayMetrics.size(14))
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Transforming with \(name). Press the cancel shortcut to stop.")

            case .transformNotice(let title, let detail):
                HStack(spacing: 7) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.system(size: 13))
                        .foregroundStyle(palette.warmSignal)
                        .accessibilityHidden(true)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(title)
                            .font(DS.Fonts.ui(11.5, .semibold))
                            .foregroundStyle(overlayTextColor)
                        Text(detail)
                            .font(DS.Fonts.ui(10.5))
                            .foregroundStyle(palette.secondaryText)
                    }
                    .lineLimit(1)
                }
                .padding(.horizontal, 9)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("\(title). \(detail)")
            }
        }
    }
}
