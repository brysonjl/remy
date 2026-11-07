import Foundation
import XCTest

/// Speech recordings are optional local inputs, never test-bundle resources.
/// Supplying a directory is separate from opting in to a model-backed test.
nonisolated enum ExternalSpeechFixtures {
    static let environmentKey = "REMY_SPEECH_FIXTURES_DIR"

    enum ConfigurationError: Error {
        case directoryMustBeAbsolute
        case directoryUnavailable
        case invalidFilename
        case fixtureMustBeRegularFile
    }

    static func directory(environment: [String: String] = ProcessInfo.processInfo.environment) throws -> URL {
        guard let value = environment[environmentKey], !value.isEmpty else {
            throw XCTSkip("Optional speech fixtures omitted. Set REMY_SPEECH_FIXTURES_DIR to a local fixture directory and enable the relevant ASR test flag.")
        }
        guard value.hasPrefix("/") else { throw ConfigurationError.directoryMustBeAbsolute }
        let url = URL(fileURLWithPath: value, isDirectory: true)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw ConfigurationError.directoryUnavailable
        }
        return url
    }

    static func url(named name: String, extension fileExtension: String = "wav",
                    environment: [String: String] = ProcessInfo.processInfo.environment) throws -> URL {
        guard !name.isEmpty, !fileExtension.isEmpty,
              ![name, fileExtension].contains(where: { $0.contains("/") || $0.contains("\\") || $0 == "." || $0 == ".." }) else {
            throw ConfigurationError.invalidFilename
        }
        let root = try directory(environment: environment)
        let url = root.appendingPathComponent(name).appendingPathExtension(fileExtension)
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw XCTSkip("Optional local speech fixture \(name).\(fileExtension) is absent; see docs/engineering/asset-provenance.md.")
        }
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw ConfigurationError.fixtureMustBeRegularFile
        }
        return url
    }
}
