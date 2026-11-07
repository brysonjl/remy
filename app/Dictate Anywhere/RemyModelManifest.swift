import CryptoKit
import Foundation

/// Reviewed artifact identities, independent of an SDK's mutable model registry.
nonisolated struct RemyModelManifest: Codable, Equatable, Sendable {
    struct Artifact: Codable, Equatable, Sendable {
        let path: String
        let size: Int64
        let sha256: String
        let url: URL
    }

    let schemaVersion: Int
    let repo: String
    let revision: String
    let license: String
    let fluidAudioVersion: String
    let fluidAudioRevision: String
    let totalBytes: Int64
    let files: [Artifact]

    enum CodingKeys: String, CodingKey {
        case schemaVersion = "schema_version", repo, revision, license, files
        case fluidAudioVersion = "fluid_audio_version"
        case fluidAudioRevision = "fluid_audio_revision"
        case totalBytes = "total_bytes"
    }

    var modelID: String { String(repo.split(separator: "/").last ?? "") }

    /// Semantic digest of the validated manifest used in local activation records.
    /// Original upstream JSON may carry extra provenance fields; it is preserved
    /// in the bundled resource rather than being treated as executable metadata.
    var digest: String {
        get throws { SHA256.hash(data: try encoded()).map { String(format: "%02x", $0) }.joined() }
    }

    static func load(from url: URL) throws -> Self {
        let data = try Data(contentsOf: url)
        guard data.count <= 2 * 1024 * 1024 else { throw RemyModelStoreError.invalidManifest("Manifest too large") }
        let value = try JSONDecoder().decode(Self.self, from: data)
        try value.validate()
        return value
    }

    static func loadBundledV2(bundle: Bundle = .main) throws -> Self {
        guard let url = bundle.url(forResource: "parakeet-v2", withExtension: "json", subdirectory: "ModelManifests")
            ?? bundle.url(forResource: "parakeet-v2", withExtension: "json") else {
            throw RemyModelStoreError.invalidManifest("Missing bundled Parakeet v2 manifest")
        }
        let value = try load(from: url)
        guard value.repo == "FluidInference/parakeet-tdt-0.6b-v2-coreml",
              value.revision == "ee09c569f73759e6d44c9bd16766f477b2b36d39",
              value.fluidAudioVersion == "0.17.5",
              value.fluidAudioRevision == "0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7", value.files.count == 22,
              value.totalBytes == 464_414_912 else {
            throw RemyModelStoreError.invalidManifest("Unexpected bundled baseline")
        }
        return value
    }

    func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(self)
    }

    func validate() throws {
        guard schemaVersion == 1, !license.isEmpty, !files.isEmpty,
              Self.isHex(revision, count: 40), Self.isHex(fluidAudioRevision, count: 40),
              repo.split(separator: "/", omittingEmptySubsequences: false).count == 2,
              repo.split(separator: "/", omittingEmptySubsequences: false).allSatisfy({ Self.isComponent(String($0)) }) else {
            throw RemyModelStoreError.invalidManifest("Invalid model identity")
        }
        var seen = Set<String>()
        var bytes: Int64 = 0
        for file in files {
            let components = file.path.split(separator: "/", omittingEmptySubsequences: false)
            guard !components.isEmpty, components.allSatisfy({ Self.isComponent(String($0)) }),
                  seen.insert(file.path).inserted, file.size >= 0,
                  Self.isHex(file.sha256, count: 64),
                  file.url.absoluteString == "https://huggingface.co/\(repo)/resolve/\(revision)/\(file.path)" else {
                throw RemyModelStoreError.invalidManifest("Unsafe or unpinned artifact")
            }
            let sum = bytes.addingReportingOverflow(file.size)
            guard !sum.overflow else { throw RemyModelStoreError.invalidManifest("Size overflow") }
            bytes = sum.partialValue
        }
        guard bytes == totalBytes else { throw RemyModelStoreError.invalidManifest("Size total mismatch") }
        // A file cannot also be an ancestor directory of another artifact.
        for path in seen {
            var parent = (path as NSString).deletingLastPathComponent
            while !parent.isEmpty {
                guard !seen.contains(parent) else { throw RemyModelStoreError.invalidManifest("File/directory conflict") }
                parent = (parent as NSString).deletingLastPathComponent
            }
        }
    }

    static func isComponent(_ value: String) -> Bool {
        !value.isEmpty && value != "." && value != ".."
            && value.utf8.allSatisfy { byte in
                (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
                    || byte == 45 || byte == 46 || byte == 95
            }
    }

    private static func isHex(_ value: String, count: Int) -> Bool {
        value.count == count && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
}

nonisolated enum RemyModelStoreError: Error, Equatable {
    case invalidManifest(String)
    case busy, notInstalled, notActive, activeRevision, leasedRevision, noRollback
    case invalidInventory, integrityMismatch(String), invalidLegacyLocation, invalidActivation
    case insufficientSpace
}


extension RemyModelStoreError: LocalizedError {
    var errorDescription: String? {
        switch self {
        case .notActive:
            "This model has not been activated. Verify an existing bundle or explicitly install it in Speech Model."
        case .notInstalled: "Verified model assets are not installed."
        case .busy: "Another model operation is running."
        case .leasedRevision: "Unload the model before switching or removing its assets."
        case .activeRevision: "Deactivate the model before removing its assets."
        case .noRollback: "No previous verified model revision is available."
        case .invalidInventory, .integrityMismatch:
            "Model assets failed integrity verification. Use the explicit model installation controls to repair them."
        case .invalidActivation: "The model activation record is invalid. Restore or reinstall a verified revision."
        case .invalidLegacyLocation: "Only Remy-owned legacy model bundles can be verified."
        case .invalidManifest: "The model manifest is invalid or is not pinned to the expected revision."
        case .insufficientSpace: "There is not enough free space to stage and verify this model."
        }
    }
}
