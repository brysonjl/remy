import XCTest
import Darwin
@testable import Dictate_Anywhere

nonisolated final class RemySoundEffectsTests: XCTestCase {
    private var fixture: URL!
    private var root: URL { fixture.appendingPathComponent("Sounds", isDirectory: true) }
    override func setUpWithError() throws {
        fixture = FileManager.default.temporaryDirectory.appendingPathComponent("remy-sounds-\(UUID())")
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
    }
    override func tearDownWithError() throws { try FileManager.default.removeItem(at: fixture) }
    private func source(_ name: String = "user-selected.wav", bytes: Data = Data("fixture audio bytes".utf8)) throws -> URL {
        let url = fixture.appendingPathComponent(name)
        try bytes.write(to: url)
        return url
    }
    func testIdentifiersCannotDecodeExternalPathsURLsOrArbitraryBuiltins() {
        for id in ["/tmp/audio.wav", "file:///tmp/a.wav", "https://example.com/a.wav", "custom:../a.wav",
                   "custom:/tmp/a.wav", "builtin:../Tink", "custom:\(UUID()).wav/extra", "", "custom:\(UUID()).exe"] {
            XCTAssertFalse(RemySoundEffects.isValidIdentifier(id), id)
        }
        XCTAssertTrue(RemySoundEffects.isValidIdentifier("none"))
        for name in RemySoundEffects.builtIns { XCTAssertTrue(RemySoundEffects.isValidIdentifier("builtin:" + name)) }
    }
    func testDefaultSoundsArePurrThenPop() {
        XCTAssertEqual(RemySoundEffects.defaultStartIdentifier, "builtin:Purr")
        XCTAssertEqual(RemySoundEffects.defaultCompletionIdentifier, "builtin:Pop")
        XCTAssertTrue(RemySoundEffects.isValidIdentifier(RemySoundEffects.defaultStartIdentifier))
        XCTAssertTrue(RemySoundEffects.isValidIdentifier(RemySoundEffects.defaultCompletionIdentifier))
    }
    func testImportIsPrivateCopiedAndRestartSafeWithoutOriginal() throws {
        let input = try source()
        let original = try Data(contentsOf: input)
        let store = RemySoundEffects.Store(rootURL: root, decoder: { _ in 1.25 })
        let id = try store.importAudio(from: input)
        XCTAssertTrue(RemySoundEffects.isValidIdentifier(id))
        XCTAssertFalse(id.contains(input.lastPathComponent))
        try FileManager.default.removeItem(at: input)
        let restarted = RemySoundEffects.Store(rootURL: root, decoder: { _ in 1.25 })
        XCTAssertEqual(try restarted.data(for: id), original)
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: root.path)[.posixPermissions] as? NSNumber, NSNumber(value: 0o700))
        let saved = root.appendingPathComponent(String(id.dropFirst(7)))
        XCTAssertEqual(try FileManager.default.attributesOfItem(atPath: saved.path)[.posixPermissions] as? NSNumber, NSNumber(value: 0o600))
    }
    func testDurationValidationRejectsLongInvalidAndCorruptClipsWithoutPublishing() throws {
        let input = try source()
        enum Corrupt: Error { case bad }
        for duration in [10.01, 0, -1, Double.nan, Double.infinity] {
            let store = RemySoundEffects.Store(rootURL: root, decoder: { _ in duration })
            XCTAssertThrowsError(try store.importAudio(from: input))
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        }
        let corrupt = RemySoundEffects.Store(rootURL: root, decoder: { _ in throw Corrupt.bad })
        XCTAssertThrowsError(try corrupt.importAudio(from: input))
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        let boundary = RemySoundEffects.Store(rootURL: root, decoder: { _ in 10 })
        XCTAssertTrue(RemySoundEffects.isValidIdentifier(try boundary.importAudio(from: input)))
    }
    func testOversizedAndEmptySourceRejectBeforeDecoder() throws {
        let input = try source()
        let fd = open(input.path, O_WRONLY)
        XCTAssertGreaterThanOrEqual(fd, 0)
        defer { close(fd) }
        XCTAssertEqual(ftruncate(fd, off_t(RemySoundEffects.Store.maximumBytes + 1)), 0)
        let store = RemySoundEffects.Store(rootURL: root, decoder: { _ in XCTFail("Decoder called for invalid size"); return 1 })
        XCTAssertThrowsError(try store.importAudio(from: input))
        XCTAssertEqual(ftruncate(fd, 0), 0)
        XCTAssertThrowsError(try store.importAudio(from: input))
    }
    func testSymlinkSourceAndStorageRefusedWithoutChangingOutsideFile() throws {
        let input = try source()
        let alias = fixture.appendingPathComponent("alias.wav")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: input)
        let store = RemySoundEffects.Store(rootURL: root, decoder: { _ in 1 })
        XCTAssertThrowsError(try store.importAudio(from: alias))
        let outside = fixture.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        try FileManager.default.createSymbolicLink(at: root, withDestinationURL: outside)
        XCTAssertThrowsError(try store.importAudio(from: input))
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: outside.path).isEmpty)
        XCTAssertEqual(try Data(contentsOf: input), Data("fixture audio bytes".utf8))
    }
    func testMissingReplacedLinkedAndCorruptStoredAudioAreSafeFailures() throws {
        let input = try source()
        let store = RemySoundEffects.Store(rootURL: root, decoder: { _ in 1 })
        XCTAssertThrowsError(try store.data(for: "custom:\(UUID()).wav"))
        let id = try store.importAudio(from: input)
        let saved = root.appendingPathComponent(String(id.dropFirst(7)))
        let corrupt = RemySoundEffects.Store(rootURL: root, decoder: { _ in throw RemySoundEffects.SoundError.invalidAudio })
        XCTAssertThrowsError(try corrupt.data(for: id))
        try FileManager.default.removeItem(at: saved)
        try FileManager.default.createSymbolicLink(at: saved, withDestinationURL: input)
        XCTAssertThrowsError(try store.data(for: id))
        try FileManager.default.removeItem(at: saved)
        XCTAssertEqual(link(input.path, saved.path), 0)
        XCTAssertThrowsError(try store.data(for: id))
    }
    func testImportRejectsNamedStageReplacementAfterExactSnapshotValidation() throws {
        let input = try source()
        let expected = try Data(contentsOf: input)
        let storeRoot = root
        let outside = try source("outside.wav", bytes: Data("replacement".utf8))
        for symlink in [false, true] {
            let retained = fixture.appendingPathComponent("retained-\(UUID()).wav")
            let store = RemySoundEffects.Store(rootURL: root, decoder: { bytes in
                XCTAssertEqual(bytes, expected)
                let stage = try XCTUnwrap(FileManager.default.contentsOfDirectory(at: storeRoot,
                    includingPropertiesForKeys: nil).first { $0.lastPathComponent.hasPrefix(".") })
                try FileManager.default.moveItem(at: stage, to: retained)
                if symlink { try FileManager.default.createSymbolicLink(at: stage, withDestinationURL: outside) }
                else {
                    try expected.write(to: stage)
                    XCTAssertEqual(chmod(stage.path, 0o600), 0)
                }
                return 1
            })
            XCTAssertThrowsError(try store.importAudio(from: input))
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        }
        XCTAssertEqual(try Data(contentsOf: outside), Data("replacement".utf8))
    }
    func testPlaybackDataValidationReceivesTheSameSnapshotItReturnsDespitePathReplacement() throws {
        let input = try source()
        let expected = try Data(contentsOf: input)
        let initial = RemySoundEffects.Store(rootURL: root, decoder: { _ in 1 })
        let id = try initial.importAudio(from: input)
        let saved = root.appendingPathComponent(String(id.dropFirst(7)))
        let checking = RemySoundEffects.Store(rootURL: root, decoder: { bytes in
            guard bytes == expected else { throw RemySoundEffects.SoundError.invalidAudio }
            try Data("corrupt replacement".utf8).write(to: saved)
            return 1
        })
        XCTAssertEqual(try checking.data(for: id), expected)
        XCTAssertThrowsError(try checking.data(for: id))
    }
    private func wav(sampleRate: UInt32 = 8_000, channels: UInt16 = 1) -> Data {
        let frames: UInt32 = 80
        let byteCount = frames * UInt32(channels) * 2
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func u16(_ value: UInt16) { data.append(UInt8(value & 255)); data.append(UInt8(value >> 8)) }
        func u32(_ value: UInt32) { u16(UInt16(value & 65535)); u16(UInt16(value >> 16)) }
        text("RIFF"); u32(36 + byteCount); text("WAVEfmt "); u32(16)
        u16(1); u16(channels); u32(sampleRate); u32(sampleRate * UInt32(channels) * 2)
        u16(channels * 2); u16(16); text("data"); u32(byteCount)
        data.append(Data(repeating: 0, count: Int(byteCount)))
        return data
    }
    func testActualMemoryDecoderAcceptsSmallPCMAndRejectsExcessiveRateOrChannelsWithoutPlayback() throws {
        let store = RemySoundEffects.Store(rootURL: root)
        let valid = wav()
        let id = try store.importAudio(from: source("valid.wav", bytes: valid))
        XCTAssertEqual(try store.data(for: id), valid)
        XCTAssertThrowsError(try store.importAudio(from: source("high-rate.wav", bytes: wav(sampleRate: 200_000))))
        XCTAssertThrowsError(try store.importAudio(from: source("many-channels.wav", bytes: wav(channels: 9))))
    }
    func testActualDecoderRejectsNonAudioWithoutPlayingAnything() throws {
        let store = RemySoundEffects.Store(rootURL: root)
        XCTAssertThrowsError(try store.importAudio(from: source()))
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }
}
