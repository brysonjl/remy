import Foundation
import Observation

/// Explicit profile editing boundary. No Settings, standard paths, output or provider calls.
/// Removed/replaced credential entries remain staged orphans; garbage collection is separate.
@MainActor @Observable final class CleanupProfileController {
    enum ControllerError: Error, Equatable {
        case invalidProfile, invalidCredentialReference, invalidCredentialValue
        case missingProfile, conflict, revisionExhausted, credentialUnavailable, storage, stateUnknown
    }
    private(set) var collection: CleanupProfileCollection = .empty
    var selectedProfile: CleanupProfile? { collection.profiles.first { $0.id == collection.selectedID } }
    @ObservationIgnored private let repository: any CleanupProfileRepository
    @ObservationIgnored private let credentials: any CleanupProfileCredentialWriter
    @ObservationIgnored private var loaded = false

    init(repository: any CleanupProfileRepository, credentials: any CleanupProfileCredentialWriter) {
        self.repository = repository; self.credentials = credentials
    }

    func load() throws {
        let value = try read()
        collection = value; loaded = true
    }

    func migrate(snapshot: CleanupLegacyProfileSnapshot) throws {
        let before = try baseForMutation()
        // Build the exact intended value without staging credentials a second time.
        let preview = MigrationPreviewRepository(value:before)
        do { _ = try CleanupLegacyProfileMigration.migrate(snapshot,repository:preview,credentials:MigrationPreviewCredentials()) }
        catch { throw sanitized(error) }
        do {
            _ = try CleanupLegacyProfileMigration.migrate(snapshot,repository:repository,credentials:credentials)
        } catch let error as CleanupProfileFileRepository.RepositoryError where error == .publicationUncertain {
            // Visible bytes do not prove the failed publication fsync reached durable storage.
            // Preserve the old observable value; the caller must explicitly reload/recover.
            throw ControllerError.stateUnknown
        } catch { throw sanitized(error) }
        // A successful helper can also mean alreadyMigrated/existingProfilesPreserved.
        // Only publish the exact intended snapshot; a racing writer never silently replaces edits.
        try reconcile(preview.value)
    }

    func select(id: UUID?) throws {
        let before = try baseForMutation()
        guard id.map({ target in before.profiles.contains { $0.id == target } }) ?? true else { throw ControllerError.missingProfile }
        let intended = CleanupProfileCollection(revision:try next(before.revision),profiles:before.profiles,
            selectedID:id,completedLegacyMigrations:before.completedLegacyMigrations)
        try commit(intended,previous:before)
    }

    func save(profile: CleanupProfile, credentialValue: String? = nil) throws {
        let before = try baseForMutation()
        let endpoint: CleanupEndpointIdentity
        do { endpoint = try profile.validatedEndpoint() } catch { throw ControllerError.invalidProfile }
        let old = before.profiles.first { $0.id == profile.id }
        guard profile.revision == (try old.map { try next($0.revision) } ?? 1) else { throw ControllerError.invalidProfile }
        if let reference = profile.credential {
            guard let old, (try? old.validatedEndpoint()) == endpoint,
                  old.credential == reference else { throw ControllerError.invalidCredentialReference }
        }
        // Check revision exhaustion before staging a secret.
        let revision = try next(before.revision)
        var reference = profile.credential
        if let credentialValue {
            guard !credentialValue.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty,
                  credentialValue.utf8.count <= 65_536 else { throw ControllerError.invalidCredentialValue }
            let fresh = CleanupCredentialReference(id:UUID(),endpoint:endpoint)
            do { try credentials.set(credentialValue,for:fresh,endpoint:endpoint) }
            catch { throw ControllerError.credentialUnavailable }
            reference = fresh
        }
        let saved = CleanupProfile(id:profile.id,revision:profile.revision,name:profile.name,adapter:profile.adapter,
            baseURL:profile.baseURL,model:profile.model,prompt:profile.prompt,promptVersion:profile.promptVersion,
            timeoutSeconds:profile.timeoutSeconds,capabilities:profile.capabilities,routePolicy:profile.routePolicy,
            allowInsecureRemoteHTTP:profile.allowInsecureRemoteHTTP,credential:reference)
        var profiles = before.profiles
        if let index = profiles.firstIndex(where: { $0.id == saved.id }) { profiles[index] = saved }
        else { profiles.append(saved) }
        let intended = CleanupProfileCollection(revision:revision,profiles:profiles,selectedID:saved.id,
            completedLegacyMigrations:before.completedLegacyMigrations)
        try commit(intended,previous:before)
    }

    func remove(id: UUID) throws {
        let before = try baseForMutation()
        guard before.profiles.contains(where: { $0.id == id }) else { throw ControllerError.missingProfile }
        let profiles = before.profiles.filter { $0.id != id }
        let selected = before.selectedID == id ? profiles.first?.id : before.selectedID
        try commit(.init(revision:try next(before.revision),profiles:profiles,selectedID:selected,
            completedLegacyMigrations:before.completedLegacyMigrations),previous:before)
        // Never delete credentials here: uncertain commits and other snapshots can still refer to them.
    }

    private func baseForMutation() throws -> CleanupProfileCollection {
        let fresh = try read()
        if loaded, fresh != collection { throw ControllerError.conflict }
        return fresh
    }
    private func next(_ revision: UInt64) throws -> UInt64 {
        guard revision < UInt64.max else { throw ControllerError.revisionExhausted }
        return revision + 1
    }
    private func read() throws -> CleanupProfileCollection {
        do { return try repository.load() } catch { throw sanitized(error) }
    }
    private func commit(_ intended: CleanupProfileCollection, previous: CleanupProfileCollection) throws {
        do { try repository.commit(intended,expectedRevision:previous.revision) }
        catch let error as CleanupProfileFileRepository.RepositoryError where error == .publicationUncertain {
            // A read-back match after failed fsync is visibility evidence, not durability.
            // Never retry publication or present it as a confirmed save.
            throw ControllerError.stateUnknown
        } catch { throw sanitized(error) }
        collection = intended; loaded = true
    }
    private func reconcile(_ intended: CleanupProfileCollection) throws {
        guard let actual = try? repository.load(), actual == intended else { throw ControllerError.stateUnknown }
        collection = actual; loaded = true
    }
    private func sanitized(_ error: Error) -> ControllerError {
        if let known = error as? ControllerError { return known }
        if let known = error as? CleanupProfileCredentialError { _ = known; return .credentialUnavailable }
        if let known = error as? CleanupProfileFileRepository.RepositoryError {
            switch known {
            case .conflict: return .conflict
            case .exhaustedRevision: return .revisionExhausted
            case .publicationUncertain: return .stateUnknown
            default: return .storage
            }
        }
        return .storage
    }
}

@MainActor private final class MigrationPreviewRepository: CleanupProfileRepository {
    var value: CleanupProfileCollection
    init(value: CleanupProfileCollection) { self.value = value }
    func load() throws -> CleanupProfileCollection { value }
    func commit(_ collection: CleanupProfileCollection,expectedRevision:UInt64) throws { value = collection }
}
@MainActor private final class MigrationPreviewCredentials: CleanupProfileCredentialStore {
    func stageLegacyBinding(from source:CleanupLegacyCredentialSource,to destination:CleanupCredentialReference) throws {}
    func resolve(_ reference:CleanupCredentialReference,for endpoint:CleanupEndpointIdentity) throws -> String? { nil }
}
