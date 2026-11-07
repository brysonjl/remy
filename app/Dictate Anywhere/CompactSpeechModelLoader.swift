import FluidAudio
import Foundation

/// Load the fused speech graphs without the SDK's optional whole-CTC-repo
/// download. Vocabulary preparation installs its own small head when needed.
nonisolated enum CompactSpeechModelLoader {
    static func load(download: Bool) async throws -> AsrModels {
        let directory = RemyStoragePaths.current.modelDirectory(named: Repo.parakeetTdtCtc110m.folderName)
        try RemyStoragePaths.current.requireOwnedModelPath(directory)
        if download { _ = try await AsrModels.download(to: directory, version: .tdtCtc110m) }
        let task = Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            return try AsrModels.loadLocal(from: directory, version: .tdtCtc110m)
        }
        return try await withTaskCancellationHandler {
            let models = try await task.value
            try Task.checkCancellation()
            return models
        } onCancel: { task.cancel() }
    }
}
