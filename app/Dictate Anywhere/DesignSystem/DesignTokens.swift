import AppKit
import SwiftUI

/// Shared visual tokens for readable light and dark macOS interfaces.
/// Atomic design level: foundation.
enum DS {
    // MARK: - Colors

    enum Colors {
        /// Service-station palette: cream surfaces, navy structure and teal signals.
        static let accent = Color("AccentColor", bundle: .main)
        static let accentDeep = Color(light: 0x08777B, dark: 0x61DCCF)
        static let onAccent = Color(light: 0xFFFFFF, dark: 0x102A35)
        static let accentSoft = Color(light: 0xDCEFEB, dark: 0x153F46)
        static let bgCard = Color(light: 0xFFFDF7, dark: 0x1A2D3B)
        static let bgInset = Color(light: 0xEAEDE6, dark: 0x142532)
        static let bgSidebar = Color(light: 0xE7EBE8, dark: 0x0B1722)
        static let bgWindow = Color(light: 0xF3F1E9, dark: 0x101E2A)
        static let border = Color(light: 0xCDD9D5, dark: 0x35505F)
        static let borderSoft = Color(light: 0xE0E7DF, dark: 0x263F4D)
        static let ink = Color(light: 0x142D3B, dark: 0xF5F2E9)
        static let success = Color(light: 0x237C65, dark: 0x71DAB4)
        static let successSoft = Color(light: 0xDEEEE3, dark: 0x193E36)
        static let textSecondary = Color(light: 0x526571, dark: 0xAFBFC8)
        static let successText = Color(light: 0x22654E, dark: 0x90E3BE)
        static let panelText = Color(light: 0x155D64, dark: 0xA0E7DE)
        static let destructive = Color(light: 0xB34238, dark: 0xFF9E8E)
        static let toggleOff = Color(light: 0xB7C6C5, dark: 0x4A626D)
        static let sliderTrackRest = Color(light: 0xD2DDD7, dark: 0x324D5B)
        static let addButtonFill = Color(light: 0xDFEBE4, dark: 0x1C3943)
        static let overlayPreviewFill = Color(light: 0xE7EEE7, dark: 0x18303C)
        static let keycapShadow = Color(light: 0xB9C9C4, dark: 0x000000)
        static let footerCardFill = Color(dynamicWhiteLightAlpha: 0.5, darkAlpha: 0.08)
        static let serviceInk = Color(hex: 0x102838)
        static let serviceCream = Color(hex: 0xF5F2E9)
        static let serviceLight = Color(hex: 0x75E5D3)
        static let serviceMuted = Color(hex: 0xB1C7CF)
        static let signalWarm = Color(light: 0xAD563B, dark: 0xF1B184)
    }

    // MARK: - Typography

    enum Fonts {
        static let displayFamily = "SF Rounded"
        static let uiFamily = "SF Pro"

        /// Rounded system lettering for the friendly service identity.
        static func display(_ size: CGFloat, _ weight: Font.Weight = .semibold) -> Font {
            Font.system(size: size, weight: weight, design: .rounded)
        }

        /// Native system UI lettering for controls and longer copy.
        static func ui(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
            Font.system(size: size, weight: weight)
        }
    }

    // MARK: - Radii

    enum Radius {
        static let card: CGFloat = 16
        static let control: CGFloat = 11
        static let small: CGFloat = 8
        static let panel: CGFloat = 10
        static let window: CGFloat = 14
        static let capsule: CGFloat = 999
    }

    // MARK: - Spacing

    enum Spacing {
        /// Vertical gap between page sections.
        static let section: CGFloat = 24
        /// Gap between an overline and its card.
        static let overlineToCard: CGFloat = 10
        /// Standard row padding: vertical.
        static let rowVertical: CGFloat = 13
        /// Standard row padding: horizontal.
        static let rowHorizontal: CGFloat = 16
        /// Space between setting descriptions and their trailing controls.
        static let rowControlGap: CGFloat = 24
        static let labelToCaption: CGFloat = 4
        /// Content padding for pages: [top, trailing, bottom, leading] = [34, 44, 36, 44].
        static let contentTop: CGFloat = 34
        static let contentHorizontal: CGFloat = 44
        static let contentBottom: CGFloat = 36
    }

    // MARK: - Layout metrics

    enum Metrics {
        /// Caps reading lines on large displays while leaving controls ample room.
        static let readableContentWidth: CGFloat = 820
        static let sidebarWidth: CGFloat = 264
        static let windowWidth: CGFloat = 1120
        static let windowHeight: CGFloat = 780
    }
}

extension Color {
    /// Creates a color from a 24-bit RGB hex value, e.g. `Color(hex: 0xDE6A3B)`.
    /// Fixed appearance: identical in light and dark mode. Reserved for
    /// colors that are intentionally constant (the always-dark overlay pill).
    init(hex: UInt32, opacity: Double = 1) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: opacity
        )
    }

    /// Creates a system-appearance-aware color from light/dark hex values.
    /// Resolves via an `NSColor` dynamic provider, so SwiftUI picks the
    /// right variant as the system appearance changes — no view code needed.
    init(light: UInt32, dark: UInt32, opacity: Double = 1) {
        self.init(nsColor: .dsDynamic(
            light: .dsHex(light, opacity: opacity),
            dark: .dsHex(dark, opacity: opacity)
        ))
    }

    /// Appearance-aware color with a computed light variant (e.g. an
    /// accent-derived color, preserving catalog re-theming) and a fixed
    /// dark hex companion.
    init(light: Color, dark: UInt32, darkOpacity: Double = 1) {
        self.init(nsColor: .dsDynamic(
            light: NSColor(light),
            dark: .dsHex(dark, opacity: darkOpacity)
        ))
    }

    /// Appearance-aware white overlay with per-appearance alpha, e.g. cards
    /// that float over the sidebar in both modes.
    init(dynamicWhiteLightAlpha lightAlpha: Double, darkAlpha: Double) {
        self.init(nsColor: .dsDynamic(
            light: NSColor(white: 1, alpha: lightAlpha),
            dark: NSColor(white: 1, alpha: darkAlpha)
        ))
    }
}

private extension NSColor {
    static func dsHex(_ hex: UInt32, opacity: Double = 1) -> NSColor {
        NSColor(
            srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255,
            alpha: CGFloat(opacity)
        )
    }

    /// Dynamic color resolving `light` vs `dark` from the drawing appearance.
    static func dsDynamic(light: NSColor, dark: NSColor) -> NSColor {
        NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        }
    }
}
