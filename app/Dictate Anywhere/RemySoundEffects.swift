import AppKit
import AudioToolbox
import Darwin

/// Local-only sound choices. Persist identifiers, never imported source URLs.
nonisolated enum RemySoundEffects {
    enum Event: Equatable, Sendable { case start, completion }
    enum SoundError: Error { case invalidIdentifier, invalidAudio, tooLarge, tooLong, unsafePath, unavailable }
    static let defaultStartIdentifier = "builtin:Tink"
    static let defaultCompletionIdentifier = "builtin:Pop"
    static let builtIns = ["Tink", "Pop", "Purr", "Ping", "Glass", "Hero", "Submarine", "Funk", "Basso", "Bottle", "Blow", "Frog", "Morse", "Sosumi"]
    static let extensions = ["wav", "aiff", "aif", "caf", "mp3", "m4a"]
    static func isValidIdentifier(_ value: String) -> Bool {
        if value == "none" { return true }
        if value.hasPrefix("builtin:") { return builtIns.contains(String(value.dropFirst(8))) }
        return customName(value) != nil
    }
    private static func customName(_ identifier: String) -> String? {
        guard identifier.utf8.count <= 64, identifier.hasPrefix("custom:") else { return nil }
        let name = String(identifier.dropFirst(7))
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 2, UUID(uuidString: String(parts[0])) != nil,
              extensions.contains(String(parts[1])) else { return nil }
        return name
    }
    static func label(_ identifier: String) -> String {
        if identifier == "none" { return "None" }
        if identifier.hasPrefix("builtin:"), isValidIdentifier(identifier) { return String(identifier.dropFirst(8)) }
        return customName(identifier) == nil ? "Unavailable sound" : "Custom audio"
    }

    nonisolated struct Store: Sendable {
        static let maximumBytes = 10 * 1_024 * 1_024
        let rootURL: URL
        private let decoder: @Sendable (Data) throws -> Double
        static var current: Self {
            Self(rootURL: RemyStoragePaths.current.applicationDirectory.appendingPathComponent("Sounds", isDirectory: true))
        }
        init(rootURL: URL, decoder: @escaping @Sendable (Data) throws -> Double = Self.decodeDuration) {
            self.rootURL = rootURL; self.decoder = decoder
        }
        func importAudio(from source: URL) throws -> String {
            guard source.isFileURL, RemySoundEffects.extensions.contains(source.pathExtension.lowercased()) else { throw SoundError.invalidAudio }
            let availability = try source.resourceValues(forKeys: [.isUbiquitousItemKey, .ubiquitousItemDownloadingStatusKey, .volumeIsLocalKey])
            guard availability.volumeIsLocal == true,
                  availability.isUbiquitousItem != true || availability.ubiquitousItemDownloadingStatus == .current
                    || availability.ubiquitousItemDownloadingStatus == .downloaded else { throw SoundError.unavailable }
            let input = open(source.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard input >= 0 else { throw SoundError.unavailable }
            defer { close(input) }
            let bytes = try read(input)
            let root = try directory(create: true)
            defer { close(root) }
            let name = UUID().uuidString + "." + source.pathExtension.lowercased()
            let stage = "." + name
            let output = openat(root, stage, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard output >= 0 else { throw SoundError.unavailable }
            defer { close(output); _ = unlinkat(root, stage, 0) }
            try bytes.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let count = Darwin.write(output, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                    if count < 0, errno == EINTR { continue }
                    guard count > 0 else { throw SoundError.unavailable }
                    offset += count
                }
            }
            guard fsync(output) == 0 else { throw SoundError.unavailable }
            try validateDuration(bytes)
            var opened = stat(), named = stat()
            guard fstat(output, &opened) == 0,
                  fstatat(root, stage, &named, AT_SYMLINK_NOFOLLOW) == 0,
                  opened.st_dev == named.st_dev, opened.st_ino == named.st_ino,
                  named.st_mode & S_IFMT == S_IFREG, named.st_uid == geteuid(),
                  named.st_mode & 0o777 == 0o600, named.st_nlink == 1,
                  lseek(output, 0, SEEK_SET) == 0,
                  try read(output, requirePrivate: true) == bytes else { throw SoundError.unsafePath }
            guard renameatx_np(root, stage, root, name, UInt32(RENAME_EXCL)) == 0 else { throw SoundError.unavailable }
            guard fsync(root) == 0 else {
                _ = unlinkat(root, name, 0)
                throw SoundError.unavailable
            }
            return "custom:" + name
        }
        func data(for identifier: String) throws -> Data {
            guard let name = RemySoundEffects.customName(identifier) else { throw SoundError.invalidIdentifier }
            let root = try directory(create: false)
            defer { close(root) }
            let file = openat(root, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
            guard file >= 0 else { throw SoundError.unavailable }
            defer { close(file) }
            let bytes = try read(file, requirePrivate: true)
            try validateDuration(bytes)
            return bytes
        }
        private func validateDuration(_ bytes: Data) throws {
            let duration: Double
            do { duration = try decoder(bytes) } catch { throw SoundError.invalidAudio }
            guard duration.isFinite, duration > 0 else { throw SoundError.invalidAudio }
            guard duration <= 10 else { throw SoundError.tooLong }
        }
        private func read(_ descriptor: Int32, requirePrivate: Bool = false) throws -> Data {
            var info = stat()
            guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1 else { throw SoundError.unsafePath }
            if requirePrivate {
                guard info.st_uid == geteuid(), info.st_mode & 0o777 == 0o600 else { throw SoundError.unsafePath }
            }
            guard info.st_size > 0, info.st_size <= Self.maximumBytes else { throw SoundError.tooLarge }
            var bytes = Data(), buffer = [UInt8](repeating: 0, count: 16_384)
            while true {
                let count = buffer.withUnsafeMutableBytes { Darwin.read(descriptor, $0.baseAddress, $0.count) }
                if count < 0, errno == EINTR { continue }
                guard count >= 0 else { throw SoundError.unavailable }
                if count == 0 { return bytes }
                guard bytes.count + count <= Self.maximumBytes else { throw SoundError.tooLarge }
                bytes.append(contentsOf: buffer.prefix(count))
            }
        }
        private func directory(create: Bool) throws -> Int32 {
            guard rootURL.isFileURL, rootURL.path.hasPrefix("/"), rootURL.pathComponents.count > 2,
                  !rootURL.pathComponents.contains(where: { $0 == "." || $0 == ".." }) else { throw SoundError.unsafePath }
            var path = rootURL.path
            if path.hasPrefix("/var/") { path = "/private/var/" + String(path.dropFirst(5)) }
            let parts = path.split(separator: "/").map(String.init)
            var current = open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard current >= 0 else { throw SoundError.unavailable }
            do {
                for (index, name) in parts.enumerated() {
                    if create, index == parts.count - 1 {
                        guard mkdirat(current, name, 0o700) == 0 || errno == EEXIST else { throw SoundError.unavailable }
                    }
                    let next = openat(current, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                    guard next >= 0 else { throw SoundError.unsafePath }
                    close(current); current = next
                }
                var info = stat()
                guard fstat(current, &info) == 0, info.st_uid == geteuid(), info.st_mode & 0o777 == 0o700 else { throw SoundError.unsafePath }
                return current
            } catch { close(current); throw error }
        }
        /// Decode from this exact immutable snapshot, never a second path lookup.
        /// No audio device or playback object is created during validation.
        private static func decodeDuration(_ data: Data) throws -> Double {
            let memory = MemoryAudio(data)
            let context = Unmanaged.passUnretained(memory).toOpaque()
            var audio: AudioFileID?
            guard AudioFileOpenWithCallbacks(context, { client, position, requested, buffer, actual in
                let source = Unmanaged<MemoryAudio>.fromOpaque(client).takeUnretainedValue().data
                guard position >= 0, position <= Int64(source.count) else { actual.pointee = 0; return -50 }
                let count = min(Int(requested), source.count - Int(position))
                if count > 0 {
                    source.withUnsafeBytes { bytes in
                        buffer.copyMemory(from: bytes.baseAddress!.advanced(by: Int(position)), byteCount: count)
                    }
                }
                actual.pointee = UInt32(count)
                return noErr
            }, nil, { client in
                Int64(Unmanaged<MemoryAudio>.fromOpaque(client).takeUnretainedValue().data.count)
            }, nil, 0, &audio) == noErr, let audio else { throw SoundError.invalidAudio }
            defer { AudioFileClose(audio); withExtendedLifetime(memory) {} }
            var extended: ExtAudioFileRef?
            guard ExtAudioFileWrapAudioFileID(audio, false, &extended) == noErr, let extended else { throw SoundError.invalidAudio }
            defer { ExtAudioFileDispose(extended) }
            var format = AudioStreamBasicDescription()
            var size = UInt32(MemoryLayout.size(ofValue: format))
            guard ExtAudioFileGetProperty(extended, kExtAudioFileProperty_FileDataFormat, &size, &format) == noErr,
                  format.mSampleRate.isFinite, format.mSampleRate > 0, format.mSampleRate <= 192_000,
                  (1...8).contains(format.mChannelsPerFrame) else { throw SoundError.invalidAudio }
            var length: Int64 = 0
            size = UInt32(MemoryLayout.size(ofValue: length))
            guard ExtAudioFileGetProperty(extended, kExtAudioFileProperty_FileLengthFrames, &size, &length) == noErr,
                  length > 0, length <= 1_920_000,
                  Double(length) / format.mSampleRate <= 10 else { throw SoundError.tooLong }
            let channels = format.mChannelsPerFrame
            var pcm = AudioStreamBasicDescription(mSampleRate: format.mSampleRate,
                mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                mBytesPerPacket: channels * 4, mFramesPerPacket: 1, mBytesPerFrame: channels * 4,
                mChannelsPerFrame: channels, mBitsPerChannel: 32, mReserved: 0)
            guard ExtAudioFileSetProperty(extended, kExtAudioFileProperty_ClientDataFormat,
                UInt32(MemoryLayout.size(ofValue: pcm)), &pcm) == noErr else { throw SoundError.invalidAudio }
            // Fixed <=128 KiB decoded buffer, <=61.44 MB total PCM processed.
            var samples = [Float](repeating: 0, count: 4_096 * Int(channels))
            var total: Int64 = 0
            while true {
                var frames: UInt32 = 4_096
                let status = samples.withUnsafeMutableBytes { bytes -> OSStatus in
                    var buffers = AudioBufferList(mNumberBuffers: 1,
                        mBuffers: AudioBuffer(mNumberChannels: channels, mDataByteSize: UInt32(bytes.count), mData: bytes.baseAddress))
                    return ExtAudioFileRead(extended, &frames, &buffers)
                }
                guard status == noErr, frames <= 4_096 else { throw SoundError.invalidAudio }
                if frames == 0 { break }
                total += Int64(frames)
                guard total <= 1_920_000, Double(total) / format.mSampleRate <= 10 else { throw SoundError.tooLong }
            }
            guard total == length else { throw SoundError.invalidAudio }
            return Double(total) / format.mSampleRate
        }
        private final class MemoryAudio {
            let data: Data
            init(_ data: Data) { self.data = data }
        }

    }

    @MainActor private static var activeSound: NSSound?
    /// Returns false when the event-default fallback was needed. No provider or
    /// source URL is contacted. Master enable/volume remain the caller's policy.
    @MainActor @discardableResult
    static func play(identifier: String, event: Event, volume: Float, store: Store? = nil) -> Bool {
        let store = store ?? .current
        activeSound?.stop()
        activeSound = nil
        if identifier == "none" { return true }
        let sound: NSSound?
        var requestedAvailable = true
        if identifier.hasPrefix("builtin:"), isValidIdentifier(identifier) {
            sound = NSSound(named: String(identifier.dropFirst(8)))
        } else if let data = try? store.data(for: identifier), let custom = NSSound(data: data) {
            sound = custom
        } else {
            requestedAvailable = false
            sound = NSSound(named: event == .start ? "Tink" : "Pop")
        }
        guard let sound else { return false }
        sound.volume = volume.isFinite ? min(1, max(0, volume)) : 0.3
        activeSound = sound
        return sound.play() && requestedAvailable
    }
}
