import XCTest
@testable import Dictate_Anywhere

@MainActor
final class CleanupProfileTests: XCTestCase {
    private func legacy() -> CleanupLegacyProfileSnapshot {
        .init(migrationID: "remy-legacy-cleanup-v1", profileID: UUID(uuidString: "43A31534-E5AF-4D42-B544-9553C44807E8")!,
              name: "My cloud cleanup", adapter: .openAICompatible,
              baseURL: "https://ollama.com/v1", model: "gemma4:31b", prompt: "Preserve this exact prompt.\n第二行",
              promptVersion: 1, timeoutSeconds: 8,
              capabilities: .init(transport: .completeJSON, discovery: false, structuredOutput: false, thinkingControl: false),
              credentialSource: .init(identifier: "legacy-remy-compatible-key"))
    }

    func testLegacyMigrationPreservesCurrentProfileAndNeverResolvesSecret() throws {
        let source = legacy(), profiles = MemoryProfiles(), credentials = FakeCredentials()
        XCTAssertEqual(try CleanupLegacyProfileMigration.migrate(source, repository: profiles, credentials: credentials), .migrated)
        let saved = try XCTUnwrap(profiles.value.profiles.first)
        XCTAssertEqual(saved.baseURL, source.baseURL)
        XCTAssertEqual(saved.model, source.model)
        XCTAssertEqual(saved.prompt, source.prompt)
        XCTAssertEqual(saved.name, source.name)
        XCTAssertEqual(profiles.value.selectedID, source.profileID)
        XCTAssertEqual(saved.credential?.endpoint, try saved.validatedEndpoint())
        XCTAssertEqual(credentials.resolveCalls, 0)
        XCTAssertEqual(try CleanupLegacyProfileMigration.migrate(source, repository: profiles, credentials: credentials), .alreadyMigrated)
        XCTAssertEqual(profiles.commits, 1)
        XCTAssertEqual(credentials.stageCalls, 1)
        let encoded = try JSONEncoder().encode(profiles.value)
        XCTAssertFalse(String(decoding: encoded, as: UTF8.self).contains("deliberate-fake-secret"))
        XCTAssertEqual(try JSONDecoder().decode(CleanupProfileCollection.self, from: encoded), profiles.value)
    }

    func testFailedProfileCommitCanRetrySameCredentialBindingWithoutDeletingLegacy() throws {
        let profiles = MemoryProfiles(), credentials = FakeCredentials()
        profiles.failCommit = true
        XCTAssertThrowsError(try CleanupLegacyProfileMigration.migrate(legacy(), repository: profiles, credentials: credentials))
        XCTAssertEqual(profiles.value, .empty)
        XCTAssertEqual(credentials.destinations.count, 1)
        profiles.failCommit = false
        XCTAssertEqual(try CleanupLegacyProfileMigration.migrate(legacy(), repository: profiles, credentials: credentials), .migrated)
        XCTAssertEqual(credentials.destinations.count, 1, "Stable reference prevents a fresh orphan each retry")
        XCTAssertEqual(credentials.resolveCalls, 0)
    }

    func testFailedCredentialStageDoesNotSelectOrSaveProfile() {
        let profiles = MemoryProfiles(), credentials = FakeCredentials()
        credentials.failStage = true
        XCTAssertThrowsError(try CleanupLegacyProfileMigration.migrate(legacy(), repository: profiles, credentials: credentials))
        XCTAssertEqual(profiles.value, .empty)
        XCTAssertEqual(profiles.commits, 0)
    }

    func testExistingNamedProfilesAreNotOverwrittenByLegacyMigration() throws {
        let profiles = MemoryProfiles(), credentials = FakeCredentials()
        _ = try CleanupLegacyProfileMigration.migrate(legacy(), repository: profiles, credentials: credentials)
        let original = profiles.value
        profiles.value = .init(revision: original.revision, profiles: original.profiles,
                              selectedID: original.selectedID, completedLegacyMigrations: [])
        let before = profiles.value
        XCTAssertEqual(try CleanupLegacyProfileMigration.migrate(legacy(), repository: profiles, credentials: credentials), .existingProfilesPreserved)
        XCTAssertEqual(profiles.value, before)
        XCTAssertEqual(credentials.stageCalls, 1)
    }

    func testEndpointScopeCanonicalizesOriginButDoesNotMergePathsOrProtocols() throws {
        XCTAssertEqual(try CleanupEndpointIdentity(baseURL: "https://OLLAMA.com:443/v1/"),
                       try CleanupEndpointIdentity(baseURL: "https://ollama.com/v1"))
        XCTAssertNotEqual(try CleanupEndpointIdentity(baseURL: "https://ollama.com/v1"),
                          try CleanupEndpointIdentity(baseURL: "https://ollama.com/other"))
        for endpoint in ["https://user:password@example.com/v1", "https://example.com/v1?key=secret", "https://example.com/#fragment",
                         "file:///tmp/server", "https://example.com/v1/../other", "https://example.com/v1/%2e%2e/other", " https://example.com/v1"] {
            XCTAssertThrowsError(try CleanupEndpointIdentity(baseURL: endpoint))
        }
        XCTAssertThrowsError(try CleanupEndpointIdentity(baseURL: "http://example.com/v1"))
        XCTAssertNoThrow(try CleanupEndpointIdentity(baseURL: "http://127.0.0.1:11434"))
        XCTAssertNoThrow(try CleanupEndpointIdentity(baseURL: "http://example.com/v1", allowInsecureRemoteHTTP: true))
    }

    func testCredentialResolverRejectsAnotherEndpoint() throws {
        let credentials = FakeCredentials()
        let endpoint = try CleanupEndpointIdentity(baseURL: "https://ollama.com/v1")
        let reference = CleanupCredentialReference(id: UUID(), endpoint: endpoint)
        let other = try CleanupEndpointIdentity(baseURL: "https://example.com/v1")
        XCTAssertThrowsError(try credentials.resolve(reference, for: other))
    }

    func testStrictLocalRejectsUnknownProxyAndRemoteEvenWithLocalHostOrDigest() {
        for (endpoint, model, evidence) in [
            ("http://127.0.0.1:11434", "gemma4:31b-cloud", CleanupExecutionEvidence.verifiedLocalExecution(model: "qwen3.5:4b", modelDigest: String(repeating: "a", count: 64))),
            ("http://127.0.0.1:11434", "ordinary-name", .declaredCloudProxy),
            ("http://127.0.0.1:11434", "qwen3.5:4b", .unknown),
            ("http://127.0.0.1:11434", "qwen3.5:4b", .installedArtifactsOnly(model: "qwen3.5:4b", modelDigest: String(repeating: "a", count: 64))),
            ("https://remote.example/v1", "qwen3.5:4b", .verifiedLocalExecution(model: "qwen3.5:4b", modelDigest: String(repeating: "a", count: 64)))
        ] {
            XCTAssertThrowsError(try CleanupLocalRouteValidation.validate(baseURL: endpoint, model: model, policy: .strictLocalOnly, evidence: evidence))
        }
        XCTAssertNoThrow(try CleanupLocalRouteValidation.validate(baseURL: "http://127.0.0.1:11434", model: "qwen3.5:4b",
            policy: .strictLocalOnly, evidence: .verifiedLocalExecution(model: "qwen3.5:4b", modelDigest: String(repeating: "a", count: 64))))
        XCTAssertNoThrow(try CleanupLocalRouteValidation.validate(baseURL: "https://ollama.com/v1", model: "gemma4:31b",
            policy: .ordinary, evidence: .unknown))
    }
}

@MainActor
private final class MemoryProfiles: CleanupProfileRepository {
    enum TestError: Error { case commitFailure, conflict }
    var value = CleanupProfileCollection.empty
    var commits = 0
    var failCommit = false
    func load() throws -> CleanupProfileCollection { value }
    func commit(_ collection: CleanupProfileCollection, expectedRevision: UInt64) throws {
        if failCommit { throw TestError.commitFailure }
        guard value.revision == expectedRevision else { throw TestError.conflict }
        value = collection; commits += 1
    }
}

@MainActor
private final class FakeCredentials: CleanupProfileCredentialStore {
    enum TestError: Error { case stageFailure, endpointMismatch }
    var failStage = false
    var stageCalls = 0
    var resolveCalls = 0
    var destinations: Set<CleanupCredentialReference> = []
    func stageLegacyBinding(from source: CleanupLegacyCredentialSource, to destination: CleanupCredentialReference) throws {
        stageCalls += 1
        if failStage { throw TestError.stageFailure }
        destinations.insert(destination)
    }
    func resolve(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws -> String? {
        resolveCalls += 1
        guard reference.endpoint == endpoint else { throw TestError.endpointMismatch }
        return "deliberate-fake-secret"
    }
}
