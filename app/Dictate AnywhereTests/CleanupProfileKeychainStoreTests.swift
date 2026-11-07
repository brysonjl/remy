import Foundation
import XCTest
import Security
@testable import Dictate_Anywhere

@MainActor final class CleanupProfileKeychainStoreTests: XCTestCase {
    private let namespace = "test.remy.credentials"
    private func reference(_ endpoint: String = "https://example.test/v1", id: UUID = UUID()) throws -> CleanupCredentialReference {
        .init(id: id, endpoint: try CleanupEndpointIdentity(baseURL: endpoint))
    }
    func testEndpointMismatchNeverTouchesBackend() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        let ref = try reference()
        let other = try CleanupEndpointIdentity(baseURL: "https://different.test/v1")
        for action in [
            { _ = try store.resolve(ref, for: other) },
            { try store.set("synthetic-secret", for: ref, endpoint: other) },
            { try store.delete(ref, for: other) }
        ] {
            XCTAssertThrowsError(try action()) { XCTAssertEqual($0 as? CleanupProfileCredentialError, .endpointMismatch) }
        }
        XCTAssertTrue(backend.calls.isEmpty)
    }
    func testLegacyEndpointMissingOrMismatchedIsRejectedBeforeBackendRead() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        let destination = try reference()
        let different = try CleanupEndpointIdentity(baseURL: "https://different.test/v1")
        for source in [
            CleanupLegacyCredentialSource(identifier: "openai-compatible"),
            .init(identifier: "openai-compatible", expectedEndpoint: different),
            .init(identifier: "openrouter", expectedEndpoint: destination.endpoint)
        ] {
            XCTAssertThrowsError(try store.stageLegacyBinding(from: source, to: destination)) {
                XCTAssertEqual($0 as? CleanupProfileCredentialError, .endpointMismatch)
            }
        }
        XCTAssertTrue(backend.calls.isEmpty)
    }
    func testSecurityQueriesExplicitlyUseFileBasedExactIdentityForBothDomains() {
        let legacy = SecurityCleanupProfileKeychainBackend.query(service: "test.legacy", account: "test-account", domain: .legacy)
        let scoped = SecurityCleanupProfileKeychainBackend.query(service: "test.scoped", account: "test-account", domain: .scoped)
        for query in [legacy, scoped] {
            XCTAssertEqual(query[kSecClass as String] as? String, kSecClassGenericPassword as String)
            XCTAssertEqual(query[kSecAttrAccount as String] as? String, "test-account")
            XCTAssertEqual(query[kSecUseDataProtectionKeychain as String] as? Bool, false)
            for key in [kSecAttrSynchronizable, kSecAttrAccessible, kSecAttrAccessGroup, kSecAttrAccess] {
                XCTAssertNil(query[key as String])
            }
            XCTAssertEqual(query[kSecUseAuthenticationUI as String] as? String, kSecUseAuthenticationUIFail as String)
            XCTAssertNil(query[kSecValueData as String])
        }
    }

    func testSecurityBackendCallerOnlyACLOnAddAndValueOnlyUpdateWithoutFallback() throws {
        let operations = FakeProfileSecurityOperations()
        let factory = FakeProfileAccessFactory()
        let backend = SecurityCleanupProfileKeychainBackend(operations: operations, accessFactory: factory)
        let data = Data("synthetic".utf8)
        try backend.add(data, service: "test.scope", account: "exact-account")
        XCTAssertEqual(factory.calls, 1)
        XCTAssertEqual(operations.adds.count, 1)
        XCTAssertEqual(operations.adds[0][kSecAttrAccess as String] as? String, "fake-caller-only-access")
        try backend.update(data, service: "test.scope", account: "exact-account")
        XCTAssertEqual(Set(operations.updates[0].1.keys), [kSecValueData as String])
        try backend.delete(service: "test.scope", account: "exact-account")
        _ = try backend.read(service: "test.scope", account: "exact-account", domain: .legacy)
        for query in operations.adds + operations.updates.map({ $0.0 }) + operations.deletes + operations.reads {
            XCTAssertEqual(query[kSecAttrService as String] as? String, "test.scope")
            XCTAssertEqual(query[kSecAttrAccount as String] as? String, "exact-account")
            XCTAssertEqual(query[kSecUseDataProtectionKeychain as String] as? Bool, false)
            for key in [kSecAttrAccessible, kSecAttrAccessGroup, kSecAttrSynchronizable] { XCTAssertNil(query[key as String]) }
        }
        operations.status = errSecMissingEntitlement
        XCTAssertThrowsError(try backend.add(data, service: "test.scope", account: "exact-account")) {
            XCTAssertEqual($0 as? CleanupProfileCredentialError, .keychain(status: errSecMissingEntitlement))
        }
        XCTAssertEqual(operations.adds.count, 2, "Failure must not retry into another keychain")
    }
    func testACLFactoryFailurePreventsAnySecurityOperation() {
        let operations = FakeProfileSecurityOperations()
        let factory = FakeProfileAccessFactory()
        factory.fails = true
        let backend = SecurityCleanupProfileKeychainBackend(operations: operations, accessFactory: factory)
        XCTAssertThrowsError(try backend.add(Data("synthetic".utf8), service: "test.scope", account: "exact")) {
            XCTAssertEqual($0 as? CleanupProfileCredentialError, .keychain(status: errSecNotAvailable))
        }
        XCTAssertTrue(operations.adds.isEmpty)
        XCTAssertTrue(operations.updates.isEmpty)
        XCTAssertTrue(operations.reads.isEmpty)
        XCTAssertTrue(operations.deletes.isEmpty)
    }

    func testSetResolveDeleteAreUUIDEndpointAndNamespaceScoped() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        let first = try reference()
        let otherEndpoint = try reference("https://different.test/v1", id: first.id)
        let otherID = try reference()
        try store.set("first-synthetic", for: first, endpoint: first.endpoint)
        try store.set("second-synthetic", for: otherEndpoint, endpoint: otherEndpoint.endpoint)
        try store.set("third-synthetic", for: otherID, endpoint: otherID.endpoint)
        XCTAssertEqual(try store.resolve(first, for: first.endpoint), "first-synthetic")
        let isolated = CleanupProfileKeychainStore(bundleIdentifier: "test.other", backend: backend)
        XCTAssertNil(try isolated.resolve(first, for: first.endpoint))
        try store.set("replacement-synthetic", for: first, endpoint: first.endpoint)
        try store.delete(first, for: first.endpoint)
        try store.delete(first, for: first.endpoint)
        XCTAssertNil(try store.resolve(first, for: first.endpoint))
        XCTAssertEqual(try store.resolve(otherEndpoint, for: otherEndpoint.endpoint), "second-synthetic")
        XCTAssertEqual(try store.resolve(otherID, for: otherID.endpoint), "third-synthetic")
        for call in backend.calls where call.service == namespace + ".cleanup-profiles" {
            XCTAssertEqual(call.account.count, 36 + 1 + 64)
            XCTAssertFalse(call.account.contains("https"))
            XCTAssertFalse(call.account.contains("synthetic"))
        }
    }
    func testLegacyMigrationRetainsSourceAndIsRetrySafeWithoutOverwritingConflict() throws {
        for identifier in ["openai-compatible", "openrouter"] {
            let backend = FakeProfileKeychain()
            let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
            let endpoint = try CleanupEndpointIdentity(baseURL: identifier == "openrouter" ? "https://openrouter.ai/api/v1" : "https://example.test/v1")
            let source = CleanupLegacyCredentialSource(identifier: identifier, expectedEndpoint: endpoint)
            let legacy = FakeProfileKeychain.Key(service: namespace + "." + identifier, account: identifier + "-api-key")
            backend.values[legacy] = Data("legacy-synthetic".utf8)
            let ref = try reference(endpoint.canonicalURL)
            try store.stageLegacyBinding(from: source, to: ref)
            try store.stageLegacyBinding(from: source, to: ref)
            XCTAssertEqual(try store.resolve(ref, for: ref.endpoint), "legacy-synthetic")
            XCTAssertEqual(backend.readDomains.first, .legacy)
            XCTAssertEqual(backend.readDomains.last, .scoped)
            XCTAssertEqual(backend.values[legacy], Data("legacy-synthetic".utf8))
            try store.set("explicit-new-synthetic", for: ref, endpoint: ref.endpoint)
            XCTAssertThrowsError(try store.stageLegacyBinding(from: source, to: ref)) {
                XCTAssertEqual($0 as? CleanupProfileCredentialError, .conflictingBinding)
            }
            XCTAssertEqual(try store.resolve(ref, for: ref.endpoint), "explicit-new-synthetic")
            XCTAssertEqual(backend.values[legacy], Data("legacy-synthetic".utf8))
        }
    }
    func testUnsupportedLegacySourceDoesNotReadAnyService() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        for identifier in ["assemblyai", "other.app.openrouter", "openrouter-api-key", "", "../openrouter"] {
            XCTAssertThrowsError(try store.stageLegacyBinding(from: .init(identifier: identifier, expectedEndpoint: reference().endpoint), to: reference())) {
                XCTAssertEqual($0 as? CleanupProfileCredentialError, .unsupportedLegacySource)
            }
        }
        XCTAssertTrue(backend.calls.isEmpty)
    }
    func testStageFailureRetainsLegacyAndRetrySucceedsWithRedactedErrors() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        let legacy = FakeProfileKeychain.Key(service: namespace + ".openrouter", account: "openrouter-api-key")
        let secret = "never-in-metadata-synthetic"
        backend.values[legacy] = Data(secret.utf8)
        backend.addFailure = errSecNotAvailable
        let ref = try reference("https://openrouter.ai/api/v1")
        let source = CleanupLegacyCredentialSource(identifier: "openrouter", expectedEndpoint: ref.endpoint)
        XCTAssertThrowsError(try store.stageLegacyBinding(from: source, to: ref)) {
            XCTAssertEqual($0 as? CleanupProfileCredentialError, .keychain(status: errSecNotAvailable))
            XCTAssertFalse(String(describing: $0).contains(secret))
        }
        XCTAssertEqual(backend.values.count, 1)
        XCTAssertEqual(backend.values[legacy], Data(secret.utf8))
        backend.addFailure = nil
        try store.stageLegacyBinding(from: .init(identifier: "openrouter", expectedEndpoint: ref.endpoint), to: ref)
        XCTAssertEqual(try store.resolve(ref, for: ref.endpoint), secret)
        XCTAssertFalse(String(data: try JSONEncoder().encode(ref), encoding: .utf8)!.contains(secret))
    }
    func testMissingLegacyFailsWithoutBindingAndInvalidValuesDoNotWrite() throws {
        let backend = FakeProfileKeychain()
        let store = CleanupProfileKeychainStore(bundleIdentifier: namespace, backend: backend)
        let ref = try reference("https://openrouter.ai/api/v1")
        XCTAssertThrowsError(try store.stageLegacyBinding(from: .init(identifier: "openrouter", expectedEndpoint: ref.endpoint), to: ref)) {
            XCTAssertEqual($0 as? CleanupProfileCredentialError, .missingLegacyCredential)
        }
        for value in ["", "  \n", String(repeating: "x", count: 65_537)] {
            XCTAssertThrowsError(try store.set(value, for: ref, endpoint: ref.endpoint)) {
                XCTAssertEqual($0 as? CleanupProfileCredentialError, .invalidValue)
            }
        }
        XCTAssertTrue(backend.values.isEmpty)
    }
}

@MainActor private final class FakeProfileKeychain: CleanupProfileKeychainBackend {
    struct Key: Hashable { let service: String; let account: String }
    var values: [Key: Data] = [:]
    var calls: [Key] = []
    var addFailure: OSStatus?
    var readDomains: [CleanupProfileKeychainDomain] = []
    func read(service: String, account: String, domain: CleanupProfileKeychainDomain) throws -> Data? {
        let key = Key(service: service, account: account); calls.append(key)
        readDomains.append(domain)
        return values[key]
    }
    func add(_ data: Data, service: String, account: String) throws {
        let key = Key(service: service, account: account); calls.append(key)
        if let addFailure { throw CleanupProfileCredentialError.keychain(status: addFailure) }
        guard values[key] == nil else { throw CleanupProfileCredentialError.keychain(status: errSecDuplicateItem) }
        values[key] = data
    }
    func update(_ data: Data, service: String, account: String) throws {
        let key = Key(service: service, account: account); calls.append(key)
        guard values[key] != nil else { throw CleanupProfileCredentialError.keychain(status: errSecItemNotFound) }
        values[key] = data
    }
    func delete(service: String, account: String) throws {
        let key = Key(service: service, account: account); calls.append(key)
        values.removeValue(forKey: key)
    }
}

@MainActor private final class FakeProfileAccessFactory: CleanupProfileAccessFactory {
    var calls = 0
    var fails = false
    func makeCallerOnlyAccess() throws -> Any {
        calls += 1
        if fails { throw CleanupProfileCredentialError.keychain(status: errSecNotAvailable) }
        return "fake-caller-only-access"
    }
}
@MainActor private final class FakeProfileSecurityOperations: CleanupProfileSecurityOperations {
    var status: OSStatus = errSecSuccess
    var adds: [[String: Any]] = []
    var reads: [[String: Any]] = []
    var updates: [([String: Any], [String: Any])] = []
    var deletes: [[String: Any]] = []
    func read(_ query: [String: Any]) -> (OSStatus, Data?) {
        reads.append(query); return (status, Data("synthetic".utf8))
    }
    func add(_ query: [String: Any]) -> OSStatus { adds.append(query); return status }
    func update(_ query: [String: Any], attributes: [String: Any]) -> OSStatus {
        updates.append((query, attributes)); return status
    }
    func delete(_ query: [String: Any]) -> OSStatus { deletes.append(query); return status }
}
