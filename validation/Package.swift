// swift-tools-version: 6.2
import PackageDescription

// A dependency-free subset of the application's actual source and inherited
// tests. Relative symlinks intentionally keep these files in sync with app/.
let package = Package(
    name: "RemyOfflineValidation",
    platforms: [.macOS(.v14)],
    targets: [
        .target(
            name: "Dictate_Anywhere",
            path: "Sources/Dictate_Anywhere",
            swiftSettings: [.defaultIsolation(MainActor.self)]
        ),
        .testTarget(
            name: "OfflineValidationTests",
            dependencies: ["Dictate_Anywhere"],
            path: "Tests/OfflineValidationTests"
        )
    ],
    swiftLanguageModes: [.v5]
)
