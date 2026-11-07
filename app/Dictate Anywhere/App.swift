//
//  App.swift
//  Dictate Anywhere
//
//  Main entry point.
//

import SwiftUI

struct RemyApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    var body: some Scene {
        WindowGroup(RemyIdentity.displayName) {
            MainWindow()
                .environment(appDelegate.appState)
        }
        .defaultSize(width: MainWindowSizing.defaultWidth, height: MainWindowSizing.defaultHeight)
        .windowStyle(.hiddenTitleBar)
        .windowToolbarStyle(.unifiedCompact(showsTitle: false))
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Open \(RemyIdentity.displayName)", action: appDelegate.showMainWindow)
            }
            CommandGroup(replacing: .appSettings) {
                Button("Settings…") {
                    appDelegate.appState.selectedPage = .settings
                    appDelegate.showMainWindow()
                }
                .keyboardShortcut(",", modifiers: .command)
            }
        }
    }
}

/// Check the settings format before SwiftUI constructs AppDelegate/Settings.
/// An older build must not run legacy migrations or open credentials first.
@main
enum RemyEntryPoint {
    @MainActor
    static func main() {
        if AppDelegate.isRunningTests {
            precondition(Bundle.main.bundleIdentifier == "io.github.brysonjl.remy.testhost",
                "Run tests through scripts/remy-build.sh test so development preferences and credentials remain isolated.")
        }
        guard RemyMigrationBootstrap.permitsExclusiveLaunch(),
              RemyMigrationBootstrap.runIfNeeded() else { return }
        if !AppDelegate.isRunningTests,
           let issue = RemyStartup.configurationIssue(in: .standard)
                ?? RemyHistoryBootstrap.captureLegacy(in: .standard) {
            let application = NSApplication.shared
            application.setActivationPolicy(.regular)
            application.activate()
            let alert = NSAlert()
            alert.messageText = "Remy cannot open these settings"
            alert.informativeText = issue
            alert.addButton(withTitle: "Quit")
            alert.runModal()
            return
        }
        RemyApp.main()
    }
}

/// Display the active build identity consistently in the window and menus.
enum RemyIdentity {
    static var displayName: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? "Remy"
    }
}
