import XCTest
import SwiftUI
@testable import Dictate_Anywhere

final class DesignSystemTests: XCTestCase {

    // MARK: - Color(hex:)

    private func components(_ color: Color) -> (r: Double, g: Double, b: Double, a: Double) {
        // Pin light appearance: tokens are dynamic, and the suite must assert
        // the intended light palette regardless of the machine's setting.
        components(color, appearance: .aqua)
    }

    func testColorHexDecodesChannels() {
        let c = components(Color(hex: 0xDE6A3B))
        XCTAssertEqual(c.r, Double(0xDE) / 255, accuracy: 0.001)
        XCTAssertEqual(c.g, Double(0x6A) / 255, accuracy: 0.001)
        XCTAssertEqual(c.b, Double(0x3B) / 255, accuracy: 0.001)
        XCTAssertEqual(c.a, 1, accuracy: 0.001)
    }

    func testColorHexOpacity() {
        let c = components(Color(hex: 0x000000, opacity: 0.5))
        XCTAssertEqual(c.a, 0.5, accuracy: 0.001)
    }

    func testColorHexBlackAndWhite() {
        let black = components(Color(hex: 0x000000))
        XCTAssertEqual(black.r + black.g + black.b, 0, accuracy: 0.001)
        let white = components(Color(hex: 0xFFFFFF))
        XCTAssertEqual(white.r + white.g + white.b, 3, accuracy: 0.001)
    }

    // MARK: - Token values match the service-station specification

    func testAccentToken() {
        let c = components(DS.Colors.accent)
        XCTAssertEqual(c.r, Double(0x08) / 255, accuracy: 0.001)
        XCTAssertEqual(c.g, Double(0x7F) / 255, accuracy: 0.001)
        XCTAssertEqual(c.b, Double(0x83) / 255, accuracy: 0.001)
    }

    func testAccentFollowsAssetCatalog() {
        // DS.Colors.accent must be the catalog color, not a hardcoded hex.
        var catalog: NSColor?
        NSAppearance(named: .aqua)!.performAsCurrentDrawingAppearance {
            catalog = NSColor(named: "AccentColor")?.usingColorSpace(.sRGB)
        }
        XCTAssertNotNil(catalog)
        let c = components(DS.Colors.accent)
        XCTAssertEqual(c.r, catalog!.redComponent, accuracy: 0.001)
        XCTAssertEqual(c.g, catalog!.greenComponent, accuracy: 0.001)
        XCTAssertEqual(c.b, catalog!.blueComponent, accuracy: 0.001)
    }

    func testWindowBackgroundToken() {
        let c = components(DS.Colors.bgWindow)
        XCTAssertEqual(c.r, Double(0xF3) / 255, accuracy: 0.001)
        XCTAssertEqual(c.g, Double(0xF1) / 255, accuracy: 0.001)
        XCTAssertEqual(c.b, Double(0xE9) / 255, accuracy: 0.001)
    }

    func testSidebarBackgroundToken() {
        XCTAssertHex(components(DS.Colors.bgSidebar), 0xE7EBE8)
    }

    func testInkToken() {
        XCTAssertHex(components(DS.Colors.ink), 0x142D3B)
    }

    // MARK: - Dark scheme (system appearance)

    private func components(_ color: Color, appearance: NSAppearance.Name) -> (r: Double, g: Double, b: Double, a: Double) {
        let drawingAppearance = NSAppearance(named: appearance) ?? NSAppearance.currentDrawing()
        var result = (r: 0.0, g: 0.0, b: 0.0, a: 0.0)
        drawingAppearance.performAsCurrentDrawingAppearance {
            let ns = NSColor(color).usingColorSpace(.sRGB)!
            result = (ns.redComponent, ns.greenComponent, ns.blueComponent, ns.alphaComponent)
        }
        return result
    }

    private func XCTAssertHex(
        _ c: (r: Double, g: Double, b: Double, a: Double),
        _ hex: UInt32,
        accuracy: Double = 0.005,
        _ message: String = "",
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(c.r, Double((hex >> 16) & 0xFF) / 255, accuracy: accuracy, message, file: file, line: line)
        XCTAssertEqual(c.g, Double((hex >> 8) & 0xFF) / 255, accuracy: accuracy, message, file: file, line: line)
        XCTAssertEqual(c.b, Double(hex & 0xFF) / 255, accuracy: accuracy, message, file: file, line: line)
    }

    func testSurfaceTokensResolveLightHexUnderAqua() {
        XCTAssertHex(components(DS.Colors.bgWindow, appearance: .aqua), 0xF3F1E9)
        XCTAssertHex(components(DS.Colors.bgSidebar, appearance: .aqua), 0xE7EBE8)
        XCTAssertHex(components(DS.Colors.bgCard, appearance: .aqua), 0xFFFDF7)
        XCTAssertHex(components(DS.Colors.ink, appearance: .aqua), 0x142D3B)
    }

    func testSurfaceTokensResolveDarkHexUnderDarkAqua() {
        XCTAssertHex(components(DS.Colors.bgWindow, appearance: .darkAqua), 0x101E2A)
        XCTAssertHex(components(DS.Colors.bgSidebar, appearance: .darkAqua), 0x0B1722)
        XCTAssertHex(components(DS.Colors.bgCard, appearance: .darkAqua), 0x1A2D3B)
        XCTAssertHex(components(DS.Colors.bgInset, appearance: .darkAqua), 0x142532)
        XCTAssertHex(components(DS.Colors.ink, appearance: .darkAqua), 0xF5F2E9)
        XCTAssertHex(components(DS.Colors.border, appearance: .darkAqua), 0x35505F)
    }

    func testDarkSchemeDiffersFromLight() {
        let pairs: [Color] = [
            DS.Colors.bgWindow, DS.Colors.bgSidebar, DS.Colors.bgCard, DS.Colors.bgInset,
            DS.Colors.border, DS.Colors.borderSoft, DS.Colors.ink, DS.Colors.textSecondary,
            DS.Colors.success, DS.Colors.successSoft, DS.Colors.successText, DS.Colors.toggleOff,
            DS.Colors.sliderTrackRest, DS.Colors.addButtonFill, DS.Colors.overlayPreviewFill,
            DS.Colors.accentSoft, DS.Colors.accentDeep, DS.Colors.panelText,
            DS.Colors.destructive,
        ]
        for token in pairs {
            let light = components(token, appearance: .aqua)
            let dark = components(token, appearance: .darkAqua)
            let delta = abs(light.r - dark.r) + abs(light.g - dark.g) + abs(light.b - dark.b)
            XCTAssertGreaterThan(delta, 0.05, "token must adapt to dark appearance")
        }
    }

    func testFooterCardFillAdaptsAlpha() {
        let light = components(DS.Colors.footerCardFill, appearance: .aqua)
        let dark = components(DS.Colors.footerCardFill, appearance: .darkAqua)
        XCTAssertEqual(light.a, 0.5, accuracy: 0.01)
        XCTAssertEqual(dark.a, 0.08, accuracy: 0.01)
    }

    // MARK: - Contrast gates (WCAG AA on real pairs)

    private func relativeLuminance(_ c: (r: Double, g: Double, b: Double, a: Double)) -> Double {
        let linearize = { (v: Double) in v <= 0.03928 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
        return 0.2126 * linearize(c.r) + 0.7152 * linearize(c.g) + 0.0722 * linearize(c.b)
    }

    private func contrastRatio(_ foreground: Color, _ background: Color, appearance: NSAppearance.Name) -> Double {
        let l1 = relativeLuminance(components(foreground, appearance: appearance))
        let l2 = relativeLuminance(components(background, appearance: appearance))
        return (max(l1, l2) + 0.05) / (min(l1, l2) + 0.05)
    }

    func testDarkBodyTextContrastMeetsAA() {
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.ink, DS.Colors.bgWindow, appearance: .darkAqua), 7)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.ink, DS.Colors.bgCard, appearance: .darkAqua), 7)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.textSecondary, DS.Colors.bgCard, appearance: .darkAqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.textSecondary, DS.Colors.bgWindow, appearance: .darkAqua), 4.5)
    }

    func testDarkTintedPairContrastMeetsAA() {
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.panelText, DS.Colors.accentSoft, appearance: .darkAqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.successText, DS.Colors.successSoft, appearance: .darkAqua), 4.5)
    }

    func testDarkAccentContrastMeetsAA() {
        // Accents lightened for dark surfaces: destructive was 2.8:1 reused.
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.destructive, DS.Colors.bgCard, appearance: .darkAqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.accentDeep, DS.Colors.bgCard, appearance: .darkAqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.accentDeep, DS.Colors.bgWindow, appearance: .darkAqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.success, DS.Colors.bgCard, appearance: .darkAqua), 3)
    }

    func testLightBodyTextContrastMeetsAA() {
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.ink, DS.Colors.bgWindow, appearance: .aqua), 7)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.textSecondary, DS.Colors.bgCard, appearance: .aqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.textSecondary, DS.Colors.bgWindow, appearance: .aqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.textSecondary, DS.Colors.bgSidebar, appearance: .aqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.accentDeep, DS.Colors.bgCard, appearance: .aqua), 4.5)
        XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.accentDeep, DS.Colors.bgWindow, appearance: .aqua), 4.5)
    }
    func testFontFamiliesMatchDesign() {
        XCTAssertEqual(DS.Fonts.displayFamily, "SF Rounded")
        XCTAssertEqual(DS.Fonts.uiFamily, "SF Pro")
    }

    func testPrimaryButtonTextContrastMeetsAA() {
        for appearance: NSAppearance.Name in [.aqua, .darkAqua] {
            XCTAssertGreaterThanOrEqual(contrastRatio(DS.Colors.onAccent, DS.Colors.accentDeep, appearance: appearance), 4.5)
        }
    }

    func testMetricsMatchDesign() {
        XCTAssertEqual(DS.Metrics.sidebarWidth, 264)
        XCTAssertEqual(DS.Metrics.windowWidth, 1120)
        XCTAssertEqual(DS.Metrics.windowHeight, 780)
        XCTAssertEqual(DS.Radius.card, 16)
        XCTAssertEqual(DS.Radius.control, 11)
        XCTAssertEqual(DS.Spacing.section, 24)
        XCTAssertEqual(DS.Spacing.contentHorizontal, 44)
    }

    // MARK: - History filtering

    private func entry(_ text: String) -> TranscriptHistoryEntry {
        TranscriptHistoryEntry(id: UUID(), text: text, createdAt: Date(timeIntervalSince1970: 1_800_000_000))
    }

    func testHistoryFilterEmptyQueryReturnsAll() {
        let entries = [entry("alpha"), entry("beta")]
        XCTAssertEqual(TranscriptHistoryView.filteredEntries(entries, searchText: "").count, 2)
        XCTAssertEqual(TranscriptHistoryView.filteredEntries(entries, searchText: "   ").count, 2)
    }

    func testHistoryFilterIsCaseInsensitive() {
        let entries = [entry("Hello World"), entry("other")]
        let filtered = TranscriptHistoryView.filteredEntries(entries, searchText: "hello")
        XCTAssertEqual(filtered.map(\.text), ["Hello World"])
    }

    func testHistoryFilterNoMatches() {
        let entries = [entry("alpha")]
        XCTAssertTrue(TranscriptHistoryView.filteredEntries(entries, searchText: "zzz").isEmpty)
    }

    // MARK: - Comparable.clamped

    func testClamped() {
        XCTAssertEqual(5.clamped(to: 0...10), 5)
        XCTAssertEqual((-1).clamped(to: 0...10), 0)
        XCTAssertEqual(11.clamped(to: 0...10), 10)
        XCTAssertEqual(0.75.clamped(to: 0.0...1.0), 0.75, accuracy: 0.0001)
    }
}
