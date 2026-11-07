import Foundation

/// Canonical credential scope. The profile separately preserves the exact user
/// endpoint string. Never contains userinfo, query, fragment or credential data.
nonisolated struct CleanupEndpointIdentity: Codable, Hashable, Sendable {
    enum ValidationError: Error { case invalidEndpoint, insecureRemoteEndpoint }
    let canonicalURL: String

    init(baseURL: String, allowInsecureRemoteHTTP: Bool = false) throws {
        guard baseURL == baseURL.trimmingCharacters(in: .whitespacesAndNewlines),
              var parts = URLComponents(string: baseURL),
              let scheme = parts.scheme?.lowercased(), ["https", "http"].contains(scheme),
              let host = parts.host?.lowercased(), !host.isEmpty,
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil,
              !parts.path.split(separator: "/").contains(where: { $0 == "." || $0 == ".." }),
              parts.port.map({ (1...65535).contains($0) }) ?? true else { throw ValidationError.invalidEndpoint }
        let loopback = ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host)
        guard scheme == "https" || loopback || allowInsecureRemoteHTTP else { throw ValidationError.insecureRemoteEndpoint }
        parts.scheme = scheme
        parts.host = host
        if (scheme == "https" && parts.port == 443) || (scheme == "http" && parts.port == 80) { parts.port = nil }
        while parts.path.hasSuffix("/") { parts.path.removeLast() }
        guard let url = parts.url, url.host != nil else { throw ValidationError.invalidEndpoint }
        canonicalURL = url.absoluteString
    }

    /// Decode through the same validator; imported JSON cannot bypass validation.
    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        // Insecure endpoints may be stored only with an explicit profile choice;
        // identity decoding preserves scope but profile validation enforces that choice.
        self = try Self(baseURL: value, allowInsecureRemoteHTTP: true)
    }
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(canonicalURL)
    }
}

nonisolated struct CleanupCredentialReference: Codable, Hashable, Sendable {
    let id: UUID
    let endpoint: CleanupEndpointIdentity
}

nonisolated enum CleanupAdapterKind: String, Codable, Sendable { case ollama, openAICompatible }
nonisolated enum CleanupTransportKind: String, Codable, Sendable { case completeJSON, sse, ndjson }
nonisolated enum CleanupRoutePolicy: String, Codable, Sendable { case ordinary, strictLocalOnly }

nonisolated struct CleanupProfileCapabilities: Codable, Equatable, Sendable {
    let transport: CleanupTransportKind
    let discovery: Bool
    let structuredOutput: Bool
    let thinkingControl: Bool
}

/// Immutable, credential-free stored/session profile. Do not include provider
/// response bodies or resolved secret values when encoding/exporting this type.
nonisolated struct CleanupProfile: Codable, Equatable, Identifiable, Sendable {
    enum ValidationError: Error { case invalidProfile, credentialEndpointMismatch }
    let id: UUID
    let revision: UInt64
    let name: String
    let adapter: CleanupAdapterKind
    let baseURL: String
    let model: String
    let prompt: String
    let promptVersion: Int
    let timeoutSeconds: Double
    let capabilities: CleanupProfileCapabilities
    let routePolicy: CleanupRoutePolicy
    let allowInsecureRemoteHTTP: Bool
    let credential: CleanupCredentialReference?

    func validatedEndpoint() throws -> CleanupEndpointIdentity {
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              revision > 0, promptVersion > 0, timeoutSeconds.isFinite,
              (1...120).contains(timeoutSeconds),
              !(adapter == .ollama && capabilities.transport == .sse),
              !(adapter == .openAICompatible && capabilities.transport == .ndjson) else { throw ValidationError.invalidProfile }
        let endpoint = try CleanupEndpointIdentity(baseURL: baseURL, allowInsecureRemoteHTTP: allowInsecureRemoteHTTP)
        guard credential.map({ $0.endpoint == endpoint }) ?? true else { throw ValidationError.credentialEndpointMismatch }
        return endpoint
    }
}

nonisolated struct CleanupProfileCollection: Codable, Equatable, Sendable {
    let revision: UInt64
    let profiles: [CleanupProfile]
    let selectedID: UUID?
    /// Migration identifiers are opaque versioned labels, never old credentials.
    let completedLegacyMigrations: Set<String>

    static let empty = Self(revision: 0, profiles: [], selectedID: nil, completedLegacyMigrations: [])
}

/// Commit atomically compares the expected revision and replaces the complete
/// value. A publicationUncertain error requires reloading and reconciling the
/// exact intended value before retrying; it cannot promise that no write occurred.
@MainActor
protocol CleanupProfileRepository {
    func load() throws -> CleanupProfileCollection
    func commit(_ collection: CleanupProfileCollection, expectedRevision: UInt64) throws
}

/// Opaque reference to the existing Remy credential entry; never its value.
nonisolated struct CleanupLegacyCredentialSource: Hashable, Sendable {
    let identifier: String
    /// Captured by trusted migration code from the configured legacy route.
    /// Imported profiles cannot authorize a legacy credential binding.
    let expectedEndpoint: CleanupEndpointIdentity?
    init(identifier: String, expectedEndpoint: CleanupEndpointIdentity? = nil) {
        self.identifier = identifier
        self.expectedEndpoint = expectedEndpoint
    }
}

@MainActor
protocol CleanupProfileCredentialStore {
    /// Idempotently stage an endpoint-scoped binding, retaining the source.
    /// A failed profile commit may leave a harmless staged orphan for later cleanup.
    func stageLegacyBinding(from source: CleanupLegacyCredentialSource,
                            to destination: CleanupCredentialReference) throws
    /// Production implementations validate scope before resolving a value.
    func resolve(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws -> String?
}

@MainActor
protocol CleanupProfileCredentialWriter: CleanupProfileCredentialStore {
    func set(_ value: String, for reference: CleanupCredentialReference, endpoint: CleanupEndpointIdentity) throws
    func delete(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws
}

nonisolated struct CleanupLegacyProfileSnapshot: Sendable {
    let migrationID: String
    /// Stable across retries, including a credential-stage/profile-commit failure.
    let profileID: UUID
    let name: String
    let adapter: CleanupAdapterKind
    let baseURL: String
    let model: String
    let prompt: String
    let promptVersion: Int
    let timeoutSeconds: Double
    let capabilities: CleanupProfileCapabilities
    let credentialSource: CleanupLegacyCredentialSource?
}

@MainActor
enum CleanupLegacyProfileMigration {
    enum Outcome: Equatable { case migrated, alreadyMigrated, existingProfilesPreserved }
    enum MigrationError: Error { case invalidMigration, revisionExhausted }

    /// Explicit invocation only. No Settings/defaults/key reads or startup hook.
    /// The caller supplies a snapshot and reviewed repository/store adapters.
    static func migrate(_ legacy: CleanupLegacyProfileSnapshot,
                        repository: CleanupProfileRepository,
                        credentials: CleanupProfileCredentialStore) throws -> Outcome {
        let existing = try repository.load()
        if existing.completedLegacyMigrations.contains(legacy.migrationID) { return .alreadyMigrated }
        guard existing.profiles.isEmpty, existing.selectedID == nil else { return .existingProfilesPreserved }
        guard !legacy.migrationID.isEmpty else { throw MigrationError.invalidMigration }
        guard existing.revision < UInt64.max else { throw MigrationError.revisionExhausted }
        // Never silently add /v1, rename a model, replace a prompt or relax TLS.
        let endpoint = try CleanupEndpointIdentity(baseURL: legacy.baseURL)
        let reference = legacy.credentialSource.map { _ in
            CleanupCredentialReference(id: legacy.profileID, endpoint: endpoint)
        }
        let profile = CleanupProfile(id: legacy.profileID, revision: 1, name: legacy.name,
            adapter: legacy.adapter, baseURL: legacy.baseURL, model: legacy.model,
            prompt: legacy.prompt, promptVersion: legacy.promptVersion, timeoutSeconds: legacy.timeoutSeconds,
            capabilities: legacy.capabilities, routePolicy: .ordinary, allowInsecureRemoteHTTP: false,
            credential: reference)
        _ = try profile.validatedEndpoint()
        if let source = legacy.credentialSource, let reference {
            try credentials.stageLegacyBinding(from: source, to: reference)
        }
        var migrations = existing.completedLegacyMigrations
        migrations.insert(legacy.migrationID)
        try repository.commit(.init(revision: existing.revision + 1, profiles: [profile],
                                    selectedID: profile.id, completedLegacyMigrations: migrations),
                              expectedRevision: existing.revision)
        return .migrated
    }
}

/// Verification evidence is supplied by a later model/server inspection adapter;
/// a host string, installed artifacts, model tag or server assertion alone cannot
/// verify local execution/egress. Only a trusted inspection adapter may construct
/// verifiedLocalExecution after verifying the execution route as well as assets.
nonisolated enum CleanupExecutionEvidence: Equatable, Sendable {
    case verifiedLocalExecution(model: String, modelDigest: String)
    case installedArtifactsOnly(model: String, modelDigest: String)
    case declaredCloudProxy
    case unknown
}
nonisolated enum CleanupLocalRouteValidation {
    enum RouteError: Error { case cloudProxy, unverifiedLocation }
    static func validate(baseURL: String, model: String, policy: CleanupRoutePolicy,
                         evidence: CleanupExecutionEvidence) throws {
        guard policy == .strictLocalOnly else { return }
        let tag = model.lowercased()
        if tag.hasSuffix("-cloud") || tag.hasSuffix(":cloud") || evidence == .declaredCloudProxy { throw RouteError.cloudProxy }
        let endpoint = try CleanupEndpointIdentity(baseURL: baseURL)
        let host = URLComponents(string: endpoint.canonicalURL)?.host?.lowercased() ?? ""
        guard ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host),
              case .verifiedLocalExecution(let verifiedModel, let digest) = evidence, verifiedModel == model,
              digest.utf8.count == 64, digest.utf8.allSatisfy({ "0123456789abcdefABCDEF".utf8.contains($0) }) else { throw RouteError.unverifiedLocation }
    }
}
