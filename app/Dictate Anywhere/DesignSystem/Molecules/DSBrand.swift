import SwiftUI

/// Atom: the original robot waiter, kept small enough to support the interface.
struct DSBrandMark: View {
    /// Overall square size (66 for sidebar, 76 for About hero).
    var size: CGFloat = 36

    var body: some View {
        Image("RemyWaiter")
            .resizable()
            .scaledToFit()
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// The robot host welcomes you to Remy's service console.
struct DSBrand: View {
    var body: some View {
        HStack(spacing: 10) {
            DSBrandMark(size: 66)
            VStack(alignment: .leading, spacing: 3) {
                Text(RemyIdentity.displayName)
                    .font(DS.Fonts.display(23, .bold))
                    .foregroundStyle(DS.Colors.ink)
                Text("At your word.")
                    .font(DS.Fonts.ui(11.5, .medium))
                    .foregroundStyle(DS.Colors.textSecondary)
            }
        }
    }
}

/// Molecule: sidebar footer card showing app status and version.
struct DSFooterCard: View {
    let statusText: String
    let statusColor: Color
    let versionText: String

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 5) {
                Image(systemName: "waveform")
                Text("SERVICE STATUS")
                    .tracking(1.1)
            }
            .font(.system(size: 9, weight: .bold, design: .monospaced))
            .foregroundStyle(DS.Colors.serviceMuted)
            HStack(spacing: 8) {
                Circle()
                    .fill(statusColor)
                    .frame(width: 8, height: 8)
                    .overlay(Circle().strokeBorder(.white.opacity(0.35), lineWidth: 1))
                    .accessibilityHidden(true)
                Text(statusText)
                    .font(DS.Fonts.ui(12, .semibold))
                    .foregroundStyle(DS.Colors.serviceCream)
            }
            Text(versionText)
                .font(.system(size: 10, design: .monospaced))
                .foregroundStyle(DS.Colors.serviceMuted)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(DS.Colors.serviceInk, in: RoundedRectangle(cornerRadius: 15))
        .overlay(RoundedRectangle(cornerRadius: 15).strokeBorder(DS.Colors.serviceLight.opacity(0.22)))
        .accessibilityElement(children: .combine)
    }
}

/// Molecule: sidebar navigation item.
struct DSNavItem: View {
    @Environment(\.colorSchemeContrast) private var contrast
    @State private var isHovered = false
    let title: String
    let systemImage: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                    .font(.system(size: 13.5, weight: .medium))
                    .foregroundStyle(isSelected ? DS.Colors.accent : DS.Colors.textSecondary)
                    .frame(width: 17)
                Text(title)
                    .font(DS.Fonts.ui(13.5, isSelected ? .semibold : .medium))
                    .foregroundStyle(DS.Colors.ink)
                Spacer(minLength: 0)
            }
            .padding(.vertical, 9)
            .padding(.horizontal, 10)
            .background {
                RoundedRectangle(cornerRadius: DS.Radius.control)
                    .fill(isSelected ? DS.Colors.accentSoft : isHovered ? DS.Colors.borderSoft : .clear)
                    .shadow(color: Color.black.opacity(isSelected ? 0.08 : 0), radius: 3, x: 0, y: 1)
            }
            .overlay(alignment: .leading) {
                if isSelected {
                    Capsule()
                        .fill(DS.Colors.accentDeep)
                        .frame(width: 3, height: 18)
                        .padding(.leading, 2)
                }
            }
            .overlay {
                if isSelected && contrast == .increased {
                    RoundedRectangle(cornerRadius: DS.Radius.control)
                        .strokeBorder(DS.Colors.ink, lineWidth: 1)
                }
            }
            .contentShape(RoundedRectangle(cornerRadius: DS.Radius.control))
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
        .accessibilityLabel(title)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}
