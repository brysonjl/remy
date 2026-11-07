import Foundation
import CryptoKit
import Security

nonisolated enum CleanupProfileKeychainDomain: Equatable { case scoped, legacy }

/// Small injectable boundary. Implementations must not put values into errors.
@MainActor protocol CleanupProfileKeychainBackend {
    func read(service: String, account: String, domain: CleanupProfileKeychainDomain) throws -> Data?
    func add(_ data: Data, service: String, account: String) throws
    func update(_ data: Data, service: String, account: String) throws
    func delete(service: String, account: String) throws
}

nonisolated enum CleanupProfileCredentialError: Error, Equatable {
    case endpointMismatch, unsupportedLegacySource, invalidValue, conflictingBinding, missingLegacyCredential
    case keychain(status: OSStatus)
}

/// Each reference binds one profile UUID to one canonical endpoint. A changed
/// endpoint requires an explicit new set; resolving never carries keys across it.
@MainActor final class CleanupProfileKeychainStore: CleanupProfileCredentialWriter {
    private let namespace: String
    private let backend: any CleanupProfileKeychainBackend
    private let maximumBytes = 65_536

    init(bundleIdentifier: String = RemyStoragePaths.current.bundleIdentifier,
         backend: (any CleanupProfileKeychainBackend)? = nil) {
        // Reuse the application's identity validation, never accept a service name.
        namespace = RemyStoragePaths(applicationSupportDirectory: URL(fileURLWithPath: "/"),
                                        bundleIdentifier: bundleIdentifier).bundleIdentifier
        self.backend = backend ?? SecurityCleanupProfileKeychainBackend()
    }

    private var service: String { namespace + ".cleanup-profiles" }
    private func account(_ reference: CleanupCredentialReference) -> String {
        let digest = SHA256.hash(data: Data(reference.endpoint.canonicalURL.utf8))
            .map { String(format: "%02x", $0) }.joined()
        return reference.id.uuidString.lowercased() + "." + digest
    }
    private func validate(_ reference: CleanupCredentialReference, endpoint: CleanupEndpointIdentity) throws {
        guard reference.endpoint == endpoint else { throw CleanupProfileCredentialError.endpointMismatch }
    }
    private func validateValue(_ data: Data) throws -> String {
        guard !data.isEmpty, data.count <= maximumBytes, let value = String(data: data, encoding: .utf8),
              !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CleanupProfileCredentialError.invalidValue
        }
        return value
    }
    func resolve(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws -> String? {
        try validate(reference, endpoint: endpoint)
        guard let data = try backend.read(service: service, account: account(reference), domain: .scoped) else { return nil }
        return try validateValue(data)
    }
    func set(_ value: String, for reference: CleanupCredentialReference,
             endpoint: CleanupEndpointIdentity) throws {
        try validate(reference, endpoint: endpoint)
        let data = Data(value.utf8)
        _ = try validateValue(data)
        do { try backend.add(data, service: service, account: account(reference)) }
        catch CleanupProfileCredentialError.keychain(status: errSecDuplicateItem) {
            try backend.update(data, service: service, account: account(reference))
        }
    }
    func delete(_ reference: CleanupCredentialReference, for endpoint: CleanupEndpointIdentity) throws {
        try validate(reference, endpoint: endpoint)
        try backend.delete(service: service, account: account(reference))
    }
    func stageLegacyBinding(from source: CleanupLegacyCredentialSource,
                            to destination: CleanupCredentialReference) throws {
        guard let expected = source.expectedEndpoint, expected == destination.endpoint else {
            throw CleanupProfileCredentialError.endpointMismatch
        }
        let legacy: (service: String, account: String)
        switch source.identifier {
        case "openai-compatible": legacy = (namespace + ".openai-compatible", "openai-compatible-api-key")
        case "openrouter":
            guard expected.canonicalURL == "https://openrouter.ai/api/v1" else {
                throw CleanupProfileCredentialError.endpointMismatch
            }
            legacy = (namespace + ".openrouter", "openrouter-api-key")
        default: throw CleanupProfileCredentialError.unsupportedLegacySource
        }
        guard let data = try backend.read(service: legacy.service, account: legacy.account, domain: .legacy) else {
            throw CleanupProfileCredentialError.missingLegacyCredential
        }
        _ = try validateValue(data)
        do { try backend.add(data, service: service, account: account(destination)) }
        catch CleanupProfileCredentialError.keychain(status: errSecDuplicateItem) {
            guard try backend.read(service: service, account: account(destination), domain: .scoped) == data else {
                throw CleanupProfileCredentialError.conflictingBinding
            }
        }
    }
}

/// Low-level seams keep query/ACL tests independent of the user's Keychain.
@MainActor protocol CleanupProfileSecurityOperations {
    func read(_ query: [String: Any]) -> (OSStatus, Data?)
    func add(_ query: [String: Any]) -> OSStatus
    func update(_ query: [String: Any], attributes: [String: Any]) -> OSStatus
    func delete(_ query: [String: Any]) -> OSStatus
}
@MainActor protocol CleanupProfileAccessFactory {
    func makeCallerOnlyAccess() throws -> Any
}

/// Explicit file-based storage for the unprovisioned macOS development app.
/// No automatic data-protection fallback or change to existing item ACLs.
@MainActor final class SecurityCleanupProfileKeychainBackend: CleanupProfileKeychainBackend {
    private let operations: any CleanupProfileSecurityOperations
    private let accessFactory: any CleanupProfileAccessFactory
    init(operations: (any CleanupProfileSecurityOperations)? = nil,
         accessFactory: (any CleanupProfileAccessFactory)? = nil) {
        self.operations = operations ?? SystemCleanupProfileSecurityOperations()
        self.accessFactory = accessFactory ?? CallerOnlyCleanupProfileAccessFactory()
    }
    static func query(service: String, account: String, domain: CleanupProfileKeychainDomain) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service, kSecAttrAccount as String: account,
         kSecUseDataProtectionKeychain as String: false,
         kSecUseAuthenticationUI as String: kSecUseAuthenticationUIFail]
    }
    func read(service: String, account: String, domain: CleanupProfileKeychainDomain) throws -> Data? {
        var query = Self.query(service: service, account: account, domain: domain)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        let (status, data) = operations.read(query)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw CleanupProfileCredentialError.keychain(status: status) }
        guard let data else { throw CleanupProfileCredentialError.invalidValue }
        return data
    }
    func add(_ data: Data, service: String, account: String) throws {
        var query = Self.query(service: service, account: account, domain: .scoped)
        query[kSecValueData as String] = data
        // nil trusted-list in SecAccessCreate means only the creating app.
        // Do not confuse this with SecACLCreateWithSimpleContents' nil semantics.
        query[kSecAttrAccess as String] = try accessFactory.makeCallerOnlyAccess()
        let status = operations.add(query)
        guard status == errSecSuccess else { throw CleanupProfileCredentialError.keychain(status: status) }
    }
    func update(_ data: Data, service: String, account: String) throws {
        let status = operations.update(Self.query(service: service, account: account, domain: .scoped),
                                       attributes: [kSecValueData as String: data])
        guard status == errSecSuccess else { throw CleanupProfileCredentialError.keychain(status: status) }
    }
    func delete(service: String, account: String) throws {
        let status = operations.delete(Self.query(service: service, account: account, domain: .scoped))
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw CleanupProfileCredentialError.keychain(status: status)
        }
    }
}

@MainActor private final class CallerOnlyCleanupProfileAccessFactory: CleanupProfileAccessFactory {
    func makeCallerOnlyAccess() throws -> Any {
        var access: SecAccess?
        let status = SecAccessCreate("Remy cleanup credential" as CFString, nil, &access)
        guard status == errSecSuccess, let access else {
            throw CleanupProfileCredentialError.keychain(status: status == errSecSuccess ? errSecInternalComponent : status)
        }
        return access
    }
}
@MainActor private final class SystemCleanupProfileSecurityOperations: CleanupProfileSecurityOperations {
    func read(_ query: [String: Any]) -> (OSStatus, Data?) {
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        return (status, result as? Data)
    }
    func add(_ query: [String: Any]) -> OSStatus { SecItemAdd(query as CFDictionary, nil) }
    func update(_ query: [String: Any], attributes: [String: Any]) -> OSStatus {
        SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
    }
    func delete(_ query: [String: Any]) -> OSStatus { SecItemDelete(query as CFDictionary) }
}
