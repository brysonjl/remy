import SwiftUI

/// Explicit appearance pairs match Remy's service console even when the
/// floating overlay uses a different appearance from its settings window.
struct OverlayPalette {
    let scheme: ColorScheme

    var surface: Color { Color(hex: scheme == .dark ? 0x102838 : 0xFFFDF7) }
    var text: Color { Color(hex: scheme == .dark ? 0xF5F2E9 : 0x142D3B) }
    var secondaryText: Color { Color(hex: scheme == .dark ? 0xB1C7CF : 0x526571) }
    var accent: Color { Color(hex: scheme == .dark ? 0x75E5D3 : 0x08777B) }
    var success: Color { Color(hex: scheme == .dark ? 0x90E3BE : 0x22654E) }
    var warmSignal: Color { Color(hex: scheme == .dark ? 0xF1B184 : 0xAD563B) }
    var border: Color { Color(hex: scheme == .dark ? 0x355F6A : 0xB6D3CA) }
    var highlight: Color { Color.white.opacity(scheme == .dark ? 0.16 : 0.9) }
}
