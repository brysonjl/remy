import Foundation
@preconcurrency import AVFoundation

nonisolated struct RecoveryCaptureResult: Sendable {
    let sampleCount: Int
    let error: String?
}

/// Audio callbacks only enqueue chunks. All file access is serialized off the
/// capture and main threads; closing drains pending writes before saving history.
nonisolated final class RecoveryAudioCapture: @unchecked Sendable {
    let id: UUID
    let url: URL
    private let queue = DispatchQueue(label: "com.dictate-anywhere.recovery-audio", qos: .utility)
    private var file: AVAudioFile?
    private var sampleCount = 0
    private var writeError: String?
    private let admission = NSLock()
    private var queuedSamples = 0
    private var closed = false
    private var admissionError: String?
    private let maximumQueuedSamples: Int
    var failureMessage: String? { admission.withLock { admissionError } }

    init(id: UUID, url: URL, maximumQueuedSamples: Int = 16_000 * 30) throws {
        self.id = id
        self.url = url
        self.maximumQueuedSamples = max(1, maximumQueuedSamples)
        let format = AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1)!
        file = try AVAudioFile(forWriting: url, settings: format.settings)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    func append(_ samples: [Float]) {
        guard !samples.isEmpty else { return }
        admission.withLock {
            guard !closed, admissionError == nil else { return }
            guard samples.count <= maximumQueuedSamples - queuedSamples else {
                admissionError = "Audio recovery could not keep up. The saved recording may be incomplete."
                return
            }
            queuedSamples += samples.count
            // Enqueue under the same lock as finish so closing cannot overtake
            // an accepted chunk. Disk work stays on the writer queue.
            queue.async { [self] in
                defer { admission.withLock { queuedSamples -= samples.count } }
                guard let file, writeError == nil else { return }
                do {
                    let buffer = try makePCMBuffer(from: samples)
                    try file.write(from: buffer)
                    sampleCount += samples.count
                } catch {
                    writeError = "Audio recovery could not be written. The saved recording may be incomplete."
                    admission.withLock { admissionError = writeError }
                }
            }
        }
    }

    func finish() async -> RecoveryCaptureResult {
        await withCheckedContinuation { continuation in
            admission.withLock {
                closed = true
                queue.async { [self] in
                    file = nil
                    continuation.resume(returning: RecoveryCaptureResult(sampleCount: sampleCount, error: failureMessage))
                }
            }
        }
    }
}

/// Reads bounded chunks (ten seconds by default), including for long recordings.
nonisolated final class RecoveryAudioReader: @unchecked Sendable {
    private let file: AVAudioFile

    var format: AVAudioFormat { file.processingFormat }

    init(url: URL) throws {
        file = try AVAudioFile(forReading: url)
        guard file.processingFormat.sampleRate == 16_000,
              file.processingFormat.channelCount == 1 else { throw TranscriptionError.audioFormatError }
    }

    func nextSamples(maxSamples: Int = 160_000) throws -> [Float]? {
        guard let buffer = try nextBuffer(maxSamples: maxSamples),
              let channel = buffer.floatChannelData?[0] else { return nil }
        return Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
    }

    /// PCM consumers can avoid copying file audio through an intermediate array.
    /// As with nextSamples, the caller serializes reads.
    func nextBuffer(maxSamples: Int = 160_000) throws -> AVAudioPCMBuffer? {
        guard file.framePosition < file.length else { return nil }
        guard maxSamples > 0 else { throw TranscriptionError.audioFormatError }
        let count = AVAudioFrameCount(min(Int64(maxSamples), file.length - file.framePosition))
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: count) else {
            throw TranscriptionError.audioFormatError
        }
        try file.read(into: buffer, frameCount: count)
        return buffer.frameLength > 0 ? buffer : nil
    }
}

/// Serialized, bounded file pulls with one conversion stream, including its tail.
/// Each returned buffer owns its samples; asynchronous consumers may retain it.
nonisolated final class ConvertedRecoveryAudioReader: @unchecked Sendable {
    private let lock = NSLock()
    private let reader: RecoveryAudioReader
    private let converter: PCMStreamConverter
    private var pending: [AVAudioPCMBuffer] = []
    private var nextIndex = 0
    private var ended = false

    init(url: URL, outputFormat: AVAudioFormat) throws {
        reader = try RecoveryAudioReader(url: url)
        converter = try PCMStreamConverter(from: reader.format, to: outputFormat)
    }

    func nextBuffer() throws -> AVAudioPCMBuffer? {
        try lock.withLock {
            while nextIndex == pending.count {
                pending.removeAll(keepingCapacity: true)
                nextIndex = 0
                guard !ended else { return nil }
                if let source = try reader.nextBuffer() {
                    pending = try converter.convert(source)
                } else {
                    ended = true
                    pending = try converter.finish()
                }
            }
            defer { nextIndex += 1 }
            return pending[nextIndex]
        }
    }
}

nonisolated struct CancelledDictation: Codable, Identifiable, Equatable, Sendable {
    let id: UUID
    let createdAt: Date
    let expiresAt: Date
    let duration: TimeInterval
    let preview: String
    /// A completed recognition result captured before cancellation during cleanup.
    /// The live preview is never treated as a complete replacement for audio.
    let completedTranscript: String?
    let hasAudio: Bool
    let captureError: String?
    /// Words from earlier parts of a continued session. This file contains
    /// only the newest audio; a completedTranscript already includes the prefix.
    var transcriptPrefix: String? = nil
    var targetBundleIdentifier: String? = nil

    nonisolated static func joining(_ prefix: String, _ text: String) -> String {
        [prefix, text].map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }.joined(separator: " ")
    }
}

@Observable
@MainActor
final class DictationRecoveryStore {
    static let retention: TimeInterval = 24 * 60 * 60
    private(set) var entries: [CancelledDictation] = []
    var errorMessage: String?
    let directory: URL
    private var expiryTask: Task<Void, Never>?
    private var activeCaptureIDs: Set<UUID> = []
    private var retainedEntryIDs: Set<UUID> = []
    private var volatileEntries: [UUID: CancelledDictation] = [:]
    @ObservationIgnored private let metadataWriter: ((Data, URL) throws -> Void)?

    init(directory: URL? = nil, metadataWriter: ((Data, URL) throws -> Void)? = nil) {
        self.directory = directory ?? RemyStoragePaths.current.recoveryDirectory
        self.metadataWriter = metadataWriter
    }

    isolated deinit { expiryTask?.cancel() }

    func reload(now: Date = Date()) throws {
        let trace = PerfTrace.begin("recovery.reload")
        defer { trace.end() }
        let fm = FileManager.default
        guard fm.fileExists(atPath: directory.path) else {
            entries = volatileEntries.values.sorted { $0.createdAt > $1.createdAt }
            return
        }
        let files = try fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.contentModificationDateKey])
        entries = files.filter { $0.pathExtension == "json" && isRegularFile($0) }.compactMap { url in
            guard let data = try? Data(contentsOf: url),
                  let entry = try? JSONDecoder().decode(CancelledDictation.self, from: data),
                  url.deletingPathExtension().lastPathComponent == entry.id.uuidString else { return nil }
            return entry
        }.sorted { $0.createdAt > $1.createdAt }
        // A metadata write can fail while the closed CAF remains valid. Keep
        // this process's known text and discover orphaned audio after restart.
        for entry in volatileEntries.values {
            entries.removeAll { $0.id == entry.id }
            entries.append(entry)
        }
        var knownIDs = Set(entries.map(\.id)).union(activeCaptureIDs)
        for file in files where file.pathExtension == "caf" && isRegularFile(file) {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent),
                  !knownIDs.contains(id),
                  let modified = try file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                  now.timeIntervalSince(modified) < Self.retention else { continue }
            do {
                let audio = try AVAudioFile(forReading: file)
                guard audio.length > 0, audio.processingFormat.sampleRate == 16_000,
                      audio.processingFormat.channelCount == 1 else { continue }
                let recovered = CancelledDictation(id: id, createdAt: modified,
                    expiresAt: modified.addingTimeInterval(Self.retention), duration: Double(audio.length) / 16_000,
                    preview: "Interrupted recording", completedTranscript: nil, hasAudio: true,
                    captureError: "Recovered after an interruption. Check the transcript for missing audio.")
                entries.append(recovered); knownIDs.insert(id)
                do { try writeMetadata(recovered) }
                catch {
                    volatileEntries[id] = recovered
                    errorMessage = "Interrupted audio was found, but recovery details could not be saved. The audio has been kept."
                }
            } catch { errorMessage = "An interrupted audio file could not be opened. It has been kept for recovery." }
        }
        entries.sort { $0.createdAt > $1.createdAt }
        for entry in entries where entry.expiresAt <= now && !retainedEntryIDs.contains(entry.id) {
            try remove(id: entry.id)
        }
        // Clean up interrupted captures and unreadable metadata after the same retention period.
        let retainedIDs = Set(entries.map(\.id)).union(activeCaptureIDs)
        for file in files where ["caf", "json"].contains(file.pathExtension) {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent),
                  !retainedIDs.contains(id), fm.fileExists(atPath: file.path),
                  let modified = try file.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                  now.timeIntervalSince(modified) >= Self.retention else { continue }
            try fm.removeItem(at: file)
        }
        scheduleExpiry()
    }

    func beginCapture() throws -> RecoveryAudioCapture {
        let trace = PerfTrace.begin("recovery.captureStart")
        defer { trace.end() }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        var excludedDirectory = directory
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try excludedDirectory.setResourceValues(values)
        let id = UUID()
        let capture = try RecoveryAudioCapture(id: id, url: audioURL(id: id))
        activeCaptureIDs.insert(id)
        return capture
    }

    @discardableResult
    func preserve(
        _ capture: RecoveryAudioCapture, preview: String, completedTranscript: String?,
        transcriptPrefix: String? = nil, previousDuration: TimeInterval = 0,
        targetBundleIdentifier: String? = nil, now: Date = Date()
    ) async throws -> CancelledDictation? {
        let trace = PerfTrace.begin("recovery.preserve")
        defer { trace.end() }
        let result = await capture.finish()
        activeCaptureIDs.remove(capture.id)
        guard result.sampleCount > 0 || !preview.isEmpty || completedTranscript?.isEmpty == false
                || transcriptPrefix?.isEmpty == false else {
            try remove(id: capture.id)
            if let error = result.error { errorMessage = "Audio recovery could not be saved: \(error)" }
            return nil
        }
        let entry = CancelledDictation(
            id: capture.id, createdAt: now, expiresAt: now.addingTimeInterval(Self.retention),
            duration: previousDuration + Double(result.sampleCount) / 16_000, preview: preview,
            completedTranscript: completedTranscript, hasAudio: result.sampleCount > 0,
            captureError: result.error, transcriptPrefix: transcriptPrefix,
            targetBundleIdentifier: targetBundleIdentifier
        )
        entries.removeAll { $0.id == entry.id }
        entries.insert(entry, at: 0)
        do {
            try writeMetadata(entry)
            volatileEntries.removeValue(forKey: entry.id)
        } catch {
            volatileEntries[entry.id] = entry
            errorMessage = "Recovery details could not be saved. Existing audio and this session's text have been kept."
            scheduleExpiry()
            throw error
        }
        if let error = result.error { errorMessage = "Only part of the cancelled audio was saved: \(error)" }
        scheduleExpiry()
        return entry
    }

    func discard(_ capture: RecoveryAudioCapture) async throws {
        let trace = PerfTrace.begin("recovery.discard")
        defer { trace.end() }
        _ = await capture.finish()
        activeCaptureIDs.remove(capture.id)
        try remove(id: capture.id)
    }

    func remove(id: UUID) throws {
        guard !retainedEntryIDs.contains(id) else {
            throw NSError(domain: "DictationRecovery", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "This session is currently in use."])
        }
        for url in [audioURL(id: id), metadataURL(id: id)] where FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
        entries.removeAll { $0.id == id }
        volatileEntries.removeValue(forKey: id)
    }

    func removeAll() throws {
        for entry in entries where !retainedEntryIDs.contains(entry.id) { try remove(id: entry.id) }
        scheduleExpiry()
    }

    var isInUse: Bool { !activeCaptureIDs.isEmpty || !retainedEntryIDs.isEmpty }

    /// Called only after the explicit stored-history deletion has removed files.
    func forgetDeletedArtifacts() {
        entries = []
        volatileEntries = [:]
        errorMessage = nil
        expiryTask?.cancel()
        expiryTask = nil
    }

    /// A source must survive expiry and History actions while it is being used.
    func retain(id: UUID) {
        retainedEntryIDs.insert(id)
        scheduleExpiry()
    }

    func release(id: UUID) {
        retainedEntryIDs.remove(id)
        scheduleExpiry()
    }

    func audioURL(id: UUID) -> URL { directory.appendingPathComponent(id.uuidString + ".caf") }
    private func metadataURL(id: UUID) -> URL { directory.appendingPathComponent(id.uuidString + ".json") }

    private func writeMetadata(_ entry: CancelledDictation) throws {
        let data = try JSONEncoder().encode(entry), url = metadataURL(id: entry.id)
        if let metadataWriter { try metadataWriter(data, url) }
        else { try data.write(to: url, options: .atomic) }
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private func isRegularFile(_ url: URL) -> Bool {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.type] as? FileAttributeType) == .typeRegular
    }

    private func scheduleExpiry() {
        expiryTask?.cancel()
        guard let next = entries.filter({ !retainedEntryIDs.contains($0.id) }).map(\.expiresAt).min()
        else { expiryTask = nil; return }
        let delay = max(1, next.timeIntervalSinceNow)
        expiryTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(for: .seconds(delay)) } catch { return }
            guard let self else { return }
            do { try self.reload() } catch { self.errorMessage = error.localizedDescription }
        }
    }
}
