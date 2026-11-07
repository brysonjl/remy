//
//  SoftwareUpdater.swift
//  Dictate Anywhere
//
//  Remy development update policy.
//

/// No Sparkle controller is instantiated: this fork has no trusted update feed.
/// The pinned Sparkle dependency is retained to reproduce the inherited build.
enum SoftwareUpdater {
    static let isEnabled = false
    static let unavailableReason = "In-app update checks are disabled. Install development builds manually."
}
