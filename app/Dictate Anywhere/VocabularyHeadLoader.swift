@preconcurrency import CoreML
import FluidAudio
import Foundation

/// Load installed-only by default. Explicit future vocabulary installation may
/// opt in to fetching only the auxiliary head in the Remy-owned cache.
/// A failed SDK optional-head load must not silently choose another encoder.
nonisolated enum VocabularyHeadLoader {
    static func load(configuration: MLModelConfiguration,
                     directory: URL = RemyStoragePaths.current.modelDirectory(named: Repo.parakeetCtc110m.folderName),
                     paths: RemyStoragePaths = .current,
                     allowDownload: Bool = false,
                     progressHandler: ProgressHandler? = nil) async throws -> MLModel {
        try Task.checkCancellation()
        try paths.requireOwnedModelPath(directory)
        let name = ModelNames.ASR.ctcHeadFile
        let headURL = directory.appendingPathComponent(name)
        try paths.requireOwnedModelPath(headURL)
        let files = FileManager.default
        func isComplete() -> Bool {
            guard files.fileExists(atPath: headURL.appendingPathComponent("coremldata.bin").path),
                  let entries = files.enumerator(at: headURL, includingPropertiesForKeys: nil) else { return false }
            for case let file as URL in entries {
                do { try paths.requireOwnedModelPath(file) } catch { return false }
                if file.lastPathComponent.hasSuffix(".partial") { return false }
            }
            return true
        }
        func download() async throws {
            try await ModelHub.download(.parakeetCtc110m, subdirectory: name, to: directory,
                                        progressHandler: progressHandler)
            try Task.checkCancellation()
        }
        if !isComplete() {
            guard allowDownload else {
                throw ASRError.processingFailed("Optional CTC vocabulary head is not installed. Install vocabulary assets explicitly before preparing recognition hints.")
            }
            try await download()
        }
        progressHandler?(DownloadProgress(fractionCompleted: 1, phase: .compiling(modelName: name)))
        do {
            let model = try await MLModel.load(contentsOf: headURL, configuration: configuration)
            try Task.checkCancellation()
            return model
        } catch {
            try Task.checkCancellation()
            guard allowDownload, !ModelHub.offlineMode else { throw error }
            // Only a failed local Core ML load reaches this retry. Network
            // failures/cancellation preserve partial files for SDK resumption.
            if files.fileExists(atPath: headURL.path) { try files.removeItem(at: headURL) }
            try await download()
            progressHandler?(DownloadProgress(fractionCompleted: 1, phase: .compiling(modelName: name)))
            let model = try await MLModel.load(contentsOf: headURL, configuration: configuration)
            try Task.checkCancellation()
            return model
        }
    }
}
