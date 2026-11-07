import XCTest
@preconcurrency import AVFoundation
import CryptoKit
@testable import Dictate_Anywhere

/// Supplementary public TTS replay, never microphone capture or delivery.
@MainActor
final class RemyBaselineRuntimeTests: XCTestCase {
    private struct Fixture: Codable {
        let id: String
        let category: String
        let reference: String
        let audioFile: String
        let sha256: String
    }
    private struct Corpus: Codable {
        let corpusVersion: String
        let voice: String
        let rate: Int
        let generator: String
        let os: String
        let fixtures: [Fixture]
    }

    func testInstalledV2PublicSyntheticReplay() async throws {
        let env = ProcessInfo.processInfo.environment
        try XCTSkipUnless(env["RUN_REMY_RUNTIME_BASELINE"] == "1",
                          "Opt in with TEST_RUNNER_RUN_REMY_RUNTIME_BASELINE=1")
        let directory = URL(fileURLWithPath: try XCTUnwrap(env["REMY_RUNTIME_BASELINE_DIR"]))
        let modelManifest = URL(fileURLWithPath: try XCTUnwrap(env["REMY_RUNTIME_MODEL_MANIFEST"]))
        let modelData = try Data(contentsOf: modelManifest)
        let corpusData = try Data(contentsOf: directory.appendingPathComponent("fixtures-manifest.json"))
        let corpus = try JSONDecoder().decode(Corpus.self, from: corpusData)
        XCTAssertEqual(corpus.corpusVersion, "remy-public-synthetic-v1")
        XCTAssertEqual(corpus.fixtures.count, 20)
        guard corpus.corpusVersion == "remy-public-synthetic-v1", corpus.fixtures.count == 20 else { return }
        for fixture in corpus.fixtures {
            guard fixture.audioFile == fixture.id + ".wav", !fixture.id.contains("/"), !fixture.id.contains("..") else {
                XCTFail("Invalid synthetic fixture path"); return
            }
            let matches = digest(try Data(contentsOf: directory.appendingPathComponent(fixture.audioFile))) == fixture.sha256
            guard matches else { XCTFail("Synthetic audio checksum mismatch"); return }
        }

        let settings = Settings.shared
        let oldModel = settings.parakeetModelChoice
        let oldLanguage = settings.selectedLanguage
        let oldMode = settings.transcriptPostProcessingMode
        let oldEngine = settings.engineChoice
        defer {
            settings.parakeetModelChoice = oldModel
            settings.selectedLanguage = oldLanguage
            settings.transcriptPostProcessingMode = oldMode
            settings.engineChoice = oldEngine
            PerfTrace.clearSessionMetadata()
        }
        settings.parakeetModelChoice = .englishOnly
        settings.selectedLanguage = .english
        settings.transcriptPostProcessingMode = .none
        let engine = ParakeetEngine()
        guard engine.checkModelOnDisk(for: .englishOnly) else {
            XCTFail("Explicit runtime gate requires installed v2 assets at \(RemyStoragePaths.current.modelsDirectory.path) (bundle \(Bundle.main.bundleIdentifier ?? "nil")); replay never downloads")
            return
        }
        let output = directory.appendingPathComponent("result-\(UUID().uuidString).json")
        var samples: [[String: Any]] = []
        var report: [String: Any] = [
            "schemaVersion": 1, "startedAt": ISO8601DateFormatter().string(from: Date()),
            "corpusVersion": corpus.corpusVersion, "corpusManifestSHA256": digest(corpusData),
            "modelManifestSHA256": digest(modelData), "model": "englishOnly / Parakeet v2",
            "os": ProcessInfo.processInfo.operatingSystemVersionString,
            "processorCount": ProcessInfo.processInfo.processorCount,
            "physicalMemoryBytes": ProcessInfo.processInfo.physicalMemory,
            "voice": corpus.voice, "speechRate": corpus.rate,
            "scope": "Public synthetic file recovery replay; no microphone, hotkey, cleanup, paste or A00 closure",
            "coldDefinition": "First prepare in this test process; OS/CoreML caches uncontrolled; prepare includes internal warmup",
            "warmDefinition": "Second full corpus pass on the same prepared engine",
            "modelIntegrity": "Manifest digest identifies declared artifacts; separately run model-baseline verifier against installed assets"
        ]
        func save() throws {
            report["samples"] = samples
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output, options: .atomic)
        }
        let loadStart = ContinuousClock.now
        do {
            try await engine.prepare()
            report["prepareMilliseconds"] = elapsed(loadStart)
        } catch {
            report["prepareMilliseconds"] = elapsed(loadStart)
            report["prepareFailed"] = true
            try save()
            throw error
        }
        var failures = 0
        for pass in 0...1 {
            for (index, fixture) in corpus.fixtures.enumerated() {
                let url = directory.appendingPathComponent(fixture.audioFile)
                let file = try AVAudioFile(forReading: url)
                let started = ContinuousClock.now
                var row: [String: Any] = ["fixture": fixture.id, "category": fixture.category,
                    "pass": pass, "phase": pass == 1 ? "warm" : (index == 0 ? "firstReplay" : "initialPass"),
                    "reference": fixture.reference, "audioSeconds": Double(file.length) / file.processingFormat.sampleRate]
                row["status"] = "pending"
                samples.append(row)
                try save()
                do {
                    let transcript = try await engine.transcribeRecording(at: url)
                    row["transcript"] = transcript
                    row["status"] = transcript.isEmpty ? "empty" : "completed"
                    if transcript.isEmpty { failures += 1 }
                } catch {
                    // Error descriptions may contain paths or runtime content; store only type.
                    row["status"] = "failed"
                    row["errorType"] = String(reflecting: type(of: error))
                    failures += 1
                }
                row["elapsedMilliseconds"] = elapsed(started)
                samples[samples.count - 1] = row
                report["failureCount"] = failures
                try save()
            }
        }
        settings.engineChoice = .appleSpeech
        await engine.unloadDeselectedModel()
        settings.engineChoice = oldEngine
        report["completedAt"] = ISO8601DateFormatter().string(from: Date())
        try save()
        // No transcript print/log; only deliberately public fixtures appear in the JSON artifact.
        XCTAssertEqual(failures, 0, "Replay failures recorded in the runtime baseline JSON")
    }

    private func elapsed(_ start: ContinuousClock.Instant) -> Double {
        let duration = start.duration(to: .now).components
        return Double(duration.seconds) * 1_000 + Double(duration.attoseconds) / 1e15
    }
    private func digest(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
