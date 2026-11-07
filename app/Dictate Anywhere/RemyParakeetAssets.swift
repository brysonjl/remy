import FluidAudio
import Foundation

/// Inventory the exact Remy-owned directory that the local loader receives.
/// SDK cache helpers remap repo names and must not determine Remy's paths.
nonisolated enum RemyParakeetAssets {
    static func activeV2Directory(paths: RemyStoragePaths = .current) throws -> URL? {
        try RemyModelStore.activeDirectory(for: RemyModelManifest.loadBundledV2(), paths: paths)
    }

    static func legacyV2IsPresent(paths: RemyStoragePaths = .current) -> Bool {
        isPresent(at: paths.modelDirectory(named: ParakeetModelChoice.englishOnly.modelDirectoryName),
                  model: .englishOnly, paths: paths)
    }

    static func isPresent(
        at directory: URL,
        model: ParakeetModelChoice,
        paths: RemyStoragePaths = .current
    ) -> Bool {
        let required: Set<String>
        switch model {
        case .englishOnly: required = ModelNames.ASR.requiredModels
        case .multilingual, .multilingualUltra: required = ModelNames.ASR.requiredModelsV3()
        case .compactEnglish: required = ModelNames.ASR.requiredModelsFused
        default: return false
        }
        do {
            try paths.requireOwnedModelPath(directory, allowRoot: false)
            let directoryValues = try directory.resourceValues(forKeys: [.isDirectoryKey])
            guard directoryValues.isDirectory == true else { return false }
            for name in required {
                let component = directory.appendingPathComponent(name, isDirectory: true)
                try paths.requireOwnedModelPath(component, allowRoot: false)
                guard try component.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true else { return false }
            }
            let vocabulary = directory.appendingPathComponent(ModelNames.ASR.vocabularyFile)
            try paths.requireOwnedModelPath(vocabulary, allowRoot: false)
            return try vocabulary.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true
        } catch {
            return false
        }
    }
}
