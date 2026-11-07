import XCTest
import Foundation
import CryptoKit
import Darwin
@preconcurrency import AVFoundation
import FluidAudio
@testable import Dictate_Anywhere

/// Explicit synthetic FILE replay. This is not a microphone/capture endurance
/// test, and never invokes cleanup, cloud providers, clipboard or insertion.
@MainActor final class RemyLongDictationTests: XCTestCase {
    func testSyntheticTwentyMinuteInstalledParakeetRecoveryReplay() async throws {
        let environment = ProcessInfo.processInfo.environment
        try XCTSkipUnless(environment["RUN_REMY_LONG_DICTATION"] == "1", "Explicit long replay opt-in required")
        guard Bundle.main.bundleIdentifier == "io.github.brysonjl.remy.testhost" else {
            XCTFail("Long replay requires the isolated Remy testhost identity")
            return
        }
        let fixture = try requiredDirectory("REMY_LONG_FIXTURE_DIR", environment)
        let results = try requiredDirectory("REMY_LONG_RESULTS_DIR", environment)
        let source = try requiredDirectory("REMY_LONG_MODEL_BUNDLE", environment)
        let run = results.appendingPathComponent("long-replay-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: run, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let journal = try LongReplayJournal(directory: run)
        try journal.event("started", ["fixture": fixture.path, "mode": "synthetic-file-replay-offline"])
        for name in ["fixture.json", "audio.aiff", "reference.txt", "section-markers.json"] {
            let file = fixture.appendingPathComponent(name).standardizedFileURL
            guard file.resolvingSymlinksInPath() == file else { throw CocoaError(.fileReadNoPermission) }
        }
        let descriptor = try JSONDecoder().decode(LongSpeechFixture.self, from: Data(contentsOf: fixture.appendingPathComponent("fixture.json")))
        guard descriptor.status == "ready", descriptor.durationSeconds >= 1_200,
              descriptor.audioFile == "audio.aiff", descriptor.fixtureKind == "synthetic-say-file-replay" else {
            XCTFail("Fixture must be ready synthetic speech of at least20minutes; files retained")
            return
        }
        let audio = fixture.appendingPathComponent(descriptor.audioFile)
        let referenceURL = fixture.appendingPathComponent("reference.txt")
        let markersURL = fixture.appendingPathComponent("section-markers.json")
        XCTAssertEqual(try sha256(audio), descriptor.audioSHA256)
        XCTAssertEqual(try sha256(referenceURL), descriptor.referenceSHA256)
        XCTAssertEqual(try sha256(markersURL), descriptor.markerSHA256)
        // Hash mismatch must prevent inference, rather than merely report a test failure.
        guard try sha256(audio) == descriptor.audioSHA256,
              try sha256(referenceURL) == descriptor.referenceSHA256,
              try sha256(markersURL) == descriptor.markerSHA256 else { return }
        let audioFile = try AVAudioFile(forReading: audio)
        let duration = Double(audioFile.length) / audioFile.processingFormat.sampleRate
        guard duration >= 1_200, audioFile.processingFormat.sampleRate == 16_000,
              audioFile.processingFormat.channelCount == 1 else { throw TranscriptionError.audioFormatError }
        let reference = try String(contentsOf: referenceURL, encoding: .utf8)
        let markers = try JSONDecoder().decode([LongSpeechMarker].self, from: Data(contentsOf: markersURL))
        let manifest = try RemyModelManifest.loadBundledV2()
        // Caller supplies one approved verified asset bundle; never discover
        // production app caches or inspect any credential/history locations.
        for artifact in manifest.files {
            XCTAssertEqual(try sha256(source.appendingPathComponent(artifact.path)), artifact.sha256)
            guard try sha256(source.appendingPathComponent(artifact.path)) == artifact.sha256 else { return }
        }
        let paths = RemyStoragePaths.current
        let legacy = paths.modelDirectory(named: manifest.modelID)
        try paths.requireOwnedModelPath(legacy, allowRoot: false)
        if !FileManager.default.fileExists(atPath: legacy.path) {
            try FileManager.default.createDirectory(at: paths.modelsDirectory, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            try FileManager.default.copyItem(at: source, to: legacy)
        }
        let store = RemyModelStore(paths: paths, transport: LongReplayNoDownloads())
        _ = try await store.adoptLegacy(manifest, from: legacy)
        _ = try await store.activate(manifest)
        try journal.event("model-verified-activated", ["revision": manifest.revision, "manifestDigest": try manifest.digest])

        let settings = Settings.shared // testhost namespace only; no key property is read.
        let oldModel = settings.parakeetModelChoice
        let oldLanguage = settings.selectedLanguage
        let oldMode = settings.transcriptPostProcessingMode
        let oldVocabulary = settings.customVocabulary
        let oldOffline = ModelHub.offlineMode
        defer {
            settings.parakeetModelChoice = oldModel
            settings.selectedLanguage = oldLanguage
            settings.transcriptPostProcessingMode = oldMode
            settings.customVocabulary = oldVocabulary
            ModelHub.offlineMode = oldOffline
        }
        settings.parakeetModelChoice = .englishOnly
        settings.selectedLanguage = .english
        settings.transcriptPostProcessingMode = .none
        settings.customVocabulary = []
        XCTAssertFalse(settings.fluidAudioVocabularyEnabled, "Cleanup none must disable acoustic vocabulary")
        ModelHub.offlineMode = true
        let engine = ParakeetEngine(vadModelURL: run.appendingPathComponent("absent-vad/silero.mlmodelc"))
        engine.setSessionConfiguration(RecognitionSessionConfiguration.capture(settings: settings))
        do {
            let preparation = ContinuousClock.now
            try await engine.prepare()
            XCTAssertTrue(engine.isReady)
            try journal.event("prepared", ["milliseconds": milliseconds(preparation), "peakProcessRSSBytes": peakRSS()])
            let start = ContinuousClock.now
            try journal.event("decode-started", ["audioSeconds": duration, "audioSHA256": descriptor.audioSHA256])
            let sampler = Task { @MainActor in
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(1))
                    guard !Task.isCancelled else { break }
                    try? journal.event("resource-sample", ["elapsedMilliseconds": milliseconds(start), "peakProcessRSSBytes": peakRSS()])
                }
            }
            let transcript: String
            do { transcript = try await engine.transcribeRecording(at: audio) }
            catch { sampler.cancel(); await sampler.value; throw error }
            sampler.cancel()
            await sampler.value
            let decodeMilliseconds = milliseconds(start)
            // Publish raw immediately, before metrics/assertions. Keep it on
            // every failure; replaying this fixture never needs the microphone.
            try Data(transcript.utf8).write(to: run.appendingPathComponent("raw-transcript.txt"), options: .atomic)
            let normalized = words(transcript).joined(separator: " ")
            let missing = markers.filter { !normalized.contains(words($0.phrase).joined(separator: " ")) }
            let tail = markers.filter { $0.kind == "tail" }
            let tailPresent = !tail.isEmpty && tail.allSatisfy { normalized.contains(words($0.phrase).joined(separator: " ")) }
            let wer = wordErrorRate(reference, transcript)
            let metrics: [String: Any] = ["audioSeconds": duration, "decodeMilliseconds": decodeMilliseconds,
                "realTimeFactor": decodeMilliseconds / 1_000 / duration, "peakProcessRSSBytes": peakRSS(),
                "rawSHA256": try sha256(run.appendingPathComponent("raw-transcript.txt")),
                "referenceWords": words(reference).count, "outputWords": words(transcript).count,
                "wordErrorRate": wer, "markerCount": markers.count, "missingMarkers": missing.map(\.phrase),
                "tailPresent": tailPresent, "modelRevision": manifest.revision, "fileReplayOnly": true]
            try journal.writeMetrics(metrics)
            try journal.event("decode-finished", metrics)
            let maxWER = Double(environment["REMY_LONG_MAX_WER"] ?? "0.20") ?? 0.20
            XCTAssertTrue(maxWER.isFinite && (0...1).contains(maxWER), "WER limit must be finite0...1")
            XCTAssertFalse(transcript.isEmpty)
            XCTAssertTrue(tailPresent, "Final tail missing; raw+fixture retained at \(run.path)")
            XCTAssertLessThanOrEqual(Double(missing.count) / Double(max(1, markers.count)), 0.10,
                                     "Section coverage failed; raw+fixture retained")
            XCTAssertLessThanOrEqual(wer, maxWER, "Long replay semantic loss; raw+fixture retained at \(run.path)")
            engine.setSessionConfiguration(nil)
            await engine.unloadModelResources()
        } catch {
            try? journal.event("failed", ["category": String(describing: type(of: error)), "fixtureRetained": true])
            await engine.cancel()
            engine.setSessionConfiguration(nil)
            await engine.unloadModelResources()
            throw error
        }
    }

    /// Hardware-free recovery fixture: the final queued PCM chunk must survive
    /// close/preserve/reload exactly; this adds no wall-clock speech wait.
    func testRecoveryFixturePreservesEveryQueuedChunkAndFinalTail() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("long-recovery-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = DictationRecoveryStore(directory: root)
        let capture = try store.beginCapture()
        let chunks: [[Float]] = [(0..<16_000).map { Float($0 % 97) / 100 },
                                (0..<16_000).map { -Float($0 % 83) / 100 }, [0.125, -0.25, 0.375, -0.5, 0.625]]
        for chunk in chunks { capture.append(chunk) }
        let preserved = try await store.preserve(capture, preview: "synthetic recovery fixture", completedTranscript: nil)
        let entry = try XCTUnwrap(preserved)
        XCTAssertTrue(entry.hasAudio)
        XCTAssertNil(entry.captureError)
        let reopened = DictationRecoveryStore(directory: root)
        try reopened.reload()
        XCTAssertEqual(reopened.entries.first?.id, entry.id)
        let reader = try RecoveryAudioReader(url: reopened.audioURL(id: entry.id))
        var actual: [Float] = []
        while let samples = try reader.nextSamples(maxSamples: 7_001) { actual.append(contentsOf: samples) }
        XCTAssertEqual(actual, chunks.flatMap { $0 })
        XCTAssertEqual(Array(actual.suffix(5)), chunks[2])
        XCTAssertEqual(entry.duration, Double(actual.count) / 16_000, accuracy: 1.0 / 16_000)
    }

    private func requiredDirectory(_ key: String, _ environment: [String: String]) throws -> URL {
        guard let path = environment[key], path.hasPrefix("/") else { throw XCTSkip("Set explicit absolute \(key)") }
        let url = URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL
        var directory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &directory), directory.boolValue,
              url.resolvingSymlinksInPath() == url else { throw XCTSkip("Missing or symlinked fixture directory: \(key)") }
        return url
    }
    private func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = SHA256()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty { hash.update(data: data) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }
    private func peakRSS() -> Int64 {
        var usage = rusage()
        return getrusage(RUSAGE_SELF, &usage) == 0 ? Int64(usage.ru_maxrss) : -1
    }
    private func milliseconds(_ start: ContinuousClock.Instant) -> Double {
        let elapsed = start.duration(to: .now).components
        return Double(elapsed.seconds) * 1_000 + Double(elapsed.attoseconds) / 1e15
    }
    private func words(_ value: String) -> [String] {
        value.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
    }
    private func wordErrorRate(_ reference: String, _ output: String) -> Double {
        let expected = words(reference), actual = words(output)
        guard !expected.isEmpty else { return actual.isEmpty ? 0 : 1 }
        var previous = Array(0...actual.count)
        for (index, word) in expected.enumerated() {
            var current = Array(repeating: 0, count: actual.count + 1)
            current[0] = index + 1
            for column in actual.indices {
                current[column + 1] = min(previous[column + 1] + 1, current[column] + 1,
                                          previous[column] + (word == actual[column] ? 0 : 1))
            }
            previous = current
        }
        return Double(previous[actual.count]) / Double(expected.count)
    }
}

private nonisolated struct LongSpeechFixture: Decodable {
    let status: String
    let fixtureKind: String
    let audioFile: String
    let durationSeconds: Double
    let audioSHA256: String
    let referenceSHA256: String
    let markerSHA256: String
}
private nonisolated struct LongSpeechMarker: Decodable { let phrase: String; let kind: String }
private nonisolated struct LongReplayNoDownloads: RemyModelDownloadTransport {
    func download(_ artifact: RemyModelManifest.Artifact, to destination: URL,
                  progress: @escaping @Sendable (Int64) -> Void) async throws {
        throw URLError(.notConnectedToInternet)
    }
}
@MainActor private final class LongReplayJournal {
    private let directory: URL
    private let handle: FileHandle
    init(directory: URL) throws {
        self.directory = directory
        let url = directory.appendingPathComponent("events.jsonl")
        guard FileManager.default.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        handle = try FileHandle(forWritingTo: url)
    }
    isolated deinit { try? handle.close() }
    func event(_ phase: String, _ values: [String: Any]) throws {
        var value = values
        value["phase"] = phase
        value["timestampUTC"] = ISO8601DateFormatter().string(from: Date())
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        data.append(0x0a)
        try handle.write(contentsOf: data)
        try handle.synchronize()
    }
    func writeMetrics(_ values: [String: Any]) throws {
        try JSONSerialization.data(withJSONObject: values, options: [.prettyPrinted, .sortedKeys])
            .write(to: directory.appendingPathComponent("metrics.json"), options: .atomic)
    }
}
