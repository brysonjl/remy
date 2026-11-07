import Foundation
import XCTest
@testable import Dictate_Anywhere

@MainActor final class CleanupProfileControllerTests: XCTestCase {
    private func profile(id:UUID = UUID(),revision:UInt64 = 1,url:String = "https://example.test/v1",
                         name:String = "Synthetic",credential:CleanupCredentialReference? = nil) -> CleanupProfile {
        .init(id:id,revision:revision,name:name,adapter:.openAICompatible,baseURL:url,model:"fixture",prompt:"Synthetic instruction",promptVersion:1,timeoutSeconds:8,
            capabilities:.init(transport:.completeJSON,discovery:false,structuredOutput:false,thinkingControl:false),routePolicy:.ordinary,allowInsecureRemoteHTTP:false,credential:credential)
    }
    private func legacy() throws -> CleanupLegacyProfileSnapshot {
        .init(migrationID:"synthetic-v1",profileID:UUID(),name:"Legacy",adapter:.openAICompatible,
            baseURL:"https://example.test/v1",model:"old fixture",prompt:"Exact old prompt",promptVersion:1,timeoutSeconds:8,
            capabilities:.init(transport:.completeJSON,discovery:false,structuredOutput:false,thinkingControl:false),
            credentialSource:.init(identifier:"openai-compatible",expectedEndpoint:try CleanupEndpointIdentity(baseURL:"https://example.test/v1")))
    }
    func testSaveSelectRemoveAndRestartPreserveValuesWithoutDeletingKeys() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        try controller.load()
        let first = profile(), second = profile()
        try controller.save(profile:first,credentialValue:"first synthetic")
        let firstSaved = try XCTUnwrap(controller.selectedProfile)
        try controller.save(profile:second,credentialValue:"second synthetic")
        XCTAssertEqual(controller.selectedProfile?.id,second.id)
        try controller.select(id:first.id)
        XCTAssertEqual(controller.selectedProfile,firstSaved)
        let restarted = CleanupProfileController(repository:repository,credentials:credentials)
        try restarted.load()
        XCTAssertEqual(restarted.collection,controller.collection)
        try restarted.remove(id:first.id)
        XCTAssertEqual(restarted.selectedProfile?.id,second.id)
        XCTAssertEqual(credentials.values.count,2)
        XCTAssertEqual(credentials.deleteCalls,0)
        try restarted.remove(id:second.id)
        XCTAssertNil(restarted.selectedProfile)
        XCTAssertTrue(restarted.collection.profiles.isEmpty)
    }
    func testUnchangedEndpointCanPreserveExactReferenceButRejectForeignReferences() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        let initial = profile()
        try controller.save(profile:initial,credentialValue:"synthetic original")
        let saved = try XCTUnwrap(controller.selectedProfile)
        let kept = profile(id:saved.id,revision:2,name:"Changed",credential:saved.credential)
        try controller.save(profile:kept)
        XCTAssertEqual(controller.selectedProfile?.credential,saved.credential)
        let foreign = CleanupCredentialReference(id:UUID(),endpoint:try CleanupEndpointIdentity(baseURL:kept.baseURL))
        let before = controller.collection
        XCTAssertThrowsError(try controller.save(profile:profile(id:saved.id,revision:3,credential:foreign))) {
            XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.invalidCredentialReference)
        }
        XCTAssertEqual(controller.collection,before)
        XCTAssertEqual(credentials.setCalls,1)
    }
    func testEndpointChangeDropsOldReferenceAndNewSecretGetsFreshReference() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        let initial = profile()
        try controller.save(profile:initial,credentialValue:"old synthetic")
        let old = try XCTUnwrap(controller.selectedProfile)
        XCTAssertThrowsError(try controller.save(profile:profile(id:old.id,revision:2,url:"https://different.test/v1",credential:old.credential)))
        try controller.save(profile:profile(id:old.id,revision:2,url:"https://different.test/v1"),credentialValue:"new synthetic")
        let updated = try XCTUnwrap(controller.selectedProfile)
        XCTAssertNotEqual(updated.credential?.id,old.credential?.id)
        XCTAssertEqual(updated.credential?.endpoint,try CleanupEndpointIdentity(baseURL:updated.baseURL))
        XCTAssertEqual(credentials.values[try XCTUnwrap(old.credential)],"old synthetic")
        XCTAssertEqual(credentials.values[try XCTUnwrap(updated.credential)],"new synthetic")
        try controller.save(profile:profile(id:old.id,revision:3,url:"https://third.test/v1"))
        XCTAssertNil(controller.selectedProfile?.credential)
    }
    func testStagingFailureAndCommitFailureNeverMutateSelectedKeyOrObservableCollection() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        let initial = profile()
        try controller.save(profile:initial,credentialValue:"old synthetic")
        let old = try XCTUnwrap(controller.selectedProfile), before = controller.collection
        let update = profile(id:old.id,revision:2,credential:old.credential)
        credentials.failSet = true
        XCTAssertThrowsError(try controller.save(profile:update,credentialValue:"new synthetic")) {
            XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.credentialUnavailable)
            XCTAssertFalse(String(describing:$0).contains("new synthetic"))
        }
        XCTAssertEqual(controller.collection,before)
        credentials.failSet = false; repository.mode = .fail
        XCTAssertThrowsError(try controller.save(profile:update,credentialValue:"new synthetic"))
        XCTAssertEqual(controller.collection,before)
        XCTAssertEqual(credentials.values[try XCTUnwrap(old.credential)],"old synthetic")
        XCTAssertEqual(credentials.values.count,2) // Failed commit leaves an isolated staged orphan.
        XCTAssertEqual(credentials.deleteCalls,0)
    }
    func testStaleCollectionAndWrongProfileRevisionsFailBeforeStaging() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        try controller.load()
        let first = profile()
        try controller.save(profile:first)
        XCTAssertThrowsError(try controller.save(profile:profile(id:first.id,revision:1),credentialValue:"not staged"))
        let other = CleanupProfileController(repository:repository,credentials:credentials)
        try other.load(); try other.save(profile:profile())
        let retained = controller.collection
        XCTAssertThrowsError(try controller.select(id:first.id)) {
            XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.conflict)
        }
        XCTAssertEqual(controller.collection,retained)
        XCTAssertEqual(credentials.setCalls,0)
    }
    func testUncertainPublicationNeverClaimsDurabilityEvenWhenReadBackMatches() throws {
        for mode in [ControllerProfileRepository.Mode.publishUncertain,.uncertainWithoutPublish,.publishUncertainUnreadable] {
            let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
            let controller = CleanupProfileController(repository:repository,credentials:credentials)
            try controller.load(); repository.mode = mode
            let candidate = profile()
            XCTAssertThrowsError(try controller.save(profile:candidate,credentialValue:"synthetic")) {
                XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.stateUnknown)
            }
            XCTAssertEqual(controller.collection,.empty)
            if mode == .publishUncertain {
                // Bytes may be visible, but cannot authorize a successful-save presentation.
                XCTAssertEqual(repository.value.profiles.first?.id,candidate.id)
            }
            XCTAssertEqual(repository.commitCalls,1)
            XCTAssertEqual(credentials.setCalls,1)
        }
    }
    func testMigrationIsIdempotentAndPreservesExistingProfiles() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        let snapshot = try legacy()
        try controller.migrate(snapshot:snapshot)
        let migrated = controller.collection
        XCTAssertEqual(controller.selectedProfile?.id,snapshot.profileID)
        XCTAssertEqual(controller.selectedProfile?.prompt,snapshot.prompt)
        try controller.migrate(snapshot:snapshot)
        XCTAssertEqual(controller.collection,migrated)
        XCTAssertEqual(credentials.stageCalls,1)
        try controller.migrate(snapshot:legacy())
        XCTAssertEqual(controller.collection,migrated)
        XCTAssertEqual(credentials.stageCalls,1)
    }
    func testMigrationUncertainPreservesObservableStateAndDoesNotRetryOrRestage() throws {
        let repository = ControllerProfileRepository(), credentials = ControllerProfileCredentials()
        let controller = CleanupProfileController(repository:repository,credentials:credentials)
        repository.mode = .publishUncertain
        XCTAssertThrowsError(try controller.migrate(snapshot:legacy())) {
            XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.stateUnknown)
        }
        XCTAssertEqual(controller.collection,.empty)
        XCTAssertFalse(repository.value.profiles.isEmpty)
        XCTAssertEqual(repository.commitCalls,1)
        XCTAssertEqual(credentials.stageCalls,1)
    }
    func testMissingMigrationCredentialDoesNotCommit() throws {
        let missingRepository = ControllerProfileRepository(), missingCredentials = ControllerProfileCredentials()
        missingCredentials.missingLegacy = true
        let missing = CleanupProfileController(repository:missingRepository,credentials:missingCredentials)
        XCTAssertThrowsError(try missing.migrate(snapshot:legacy())) {
            XCTAssertEqual($0 as? CleanupProfileController.ControllerError,.credentialUnavailable)
        }
        XCTAssertEqual(missing.collection,.empty)
        XCTAssertEqual(missingRepository.commitCalls,0)
    }
}

@MainActor private final class ControllerProfileRepository: CleanupProfileRepository {
    enum Mode { case normal, fail, publishUncertain, uncertainWithoutPublish, publishUncertainUnreadable }
    var value = CleanupProfileCollection.empty
    var mode: Mode = .normal
    var unreadable = false
    var commitCalls = 0
    func load() throws -> CleanupProfileCollection {
        if unreadable { throw CleanupProfileFileRepository.RepositoryError.io }
        return value
    }
    func commit(_ collection:CleanupProfileCollection,expectedRevision:UInt64) throws {
        commitCalls += 1
        guard expectedRevision == value.revision else { throw CleanupProfileFileRepository.RepositoryError.conflict }
        if mode == .fail { throw CleanupProfileFileRepository.RepositoryError.io }
        if mode != .uncertainWithoutPublish { value = collection }
        if mode == .publishUncertainUnreadable { unreadable = true }
        if [.publishUncertain,.uncertainWithoutPublish,.publishUncertainUnreadable].contains(mode) {
            throw CleanupProfileFileRepository.RepositoryError.publicationUncertain
        }
    }
}
@MainActor private final class ControllerProfileCredentials: CleanupProfileCredentialWriter {
    var values: [CleanupCredentialReference:String] = [:]
    var failSet = false, missingLegacy = false
    var setCalls = 0, deleteCalls = 0, stageCalls = 0
    func set(_ value:String,for reference:CleanupCredentialReference,endpoint:CleanupEndpointIdentity) throws {
        setCalls += 1
        if failSet { throw CleanupProfileCredentialError.keychain(status:-1) }
        values[reference] = value
    }
    func delete(_ reference:CleanupCredentialReference,for endpoint:CleanupEndpointIdentity) throws { deleteCalls += 1 }
    func resolve(_ reference:CleanupCredentialReference,for endpoint:CleanupEndpointIdentity) throws -> String? { values[reference] }
    func stageLegacyBinding(from source:CleanupLegacyCredentialSource,to destination:CleanupCredentialReference) throws {
        stageCalls += 1
        if missingLegacy { throw CleanupProfileCredentialError.missingLegacyCredential }
        values[destination] = "legacy synthetic"
    }
}
