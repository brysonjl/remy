import XCTest
import CryptoKit
@testable import Dictate_Anywhere

/// Real-provider smoke tests; only deliberate public seeds leave this process.
@MainActor
final class RemyBaselineProviderTests: XCTestCase {
    private struct Fixture: Decodable { let id: String; let input: String }

    func testExplicitLocalOllamaPublicSeeds() async throws {
        let env = ProcessInfo.processInfo.environment
        try XCTSkipUnless(env["RUN_REMY_LOCAL_PROVIDER_BASELINE"] == "1", "Local provider replay is opt-in")
        let model = try XCTUnwrap(env["REMY_LOCAL_PROVIDER_MODEL"], "Explicit installed local model required")
        // Reject Ollama cloud-proxy model names even though the host is loopback.
        guard !model.isEmpty, !model.lowercased().contains("cloud") else {
            XCTFail("Choose an installed local model without cloud routing"); return
        }
        try await replay(model: model, cloud: false)
    }

    func testExplicitCloudOllamaPublicSeeds() async throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["RUN_REMY_CLOUD_BASELINE"] == "1", "Cloud replay is separately opt-in")
        let settings = Settings.shared
        guard settings.openAICompatibleBaseURL == "https://ollama.com/v1",
              ["gemma4:31b-cloud", "gemma4:31b"].contains(settings.openAICompatibleModel) else {
            throw XCTSkip("Configure exact Ollama cloud endpoint and model in Remy Dev first")
        }
        // Key is accessed only inside the test process through Settings/Keychain.
        try XCTSkipUnless(!settings.openAICompatibleAPIKey.isEmpty, "Enter cloud credentials in Remy Dev UI first")
        try await replay(model: settings.openAICompatibleModel, cloud: true)
    }

    private func replay(model: String, cloud: Bool) async throws {
        let env = ProcessInfo.processInfo.environment
        let source = URL(fileURLWithPath: try XCTUnwrap(env["REMY_PROVIDER_BASELINE_SOURCE_DIR"]))
        let outputDirectory = URL(fileURLWithPath: try XCTUnwrap(env["REMY_PROVIDER_BASELINE_OUTPUT_DIR"]))
        let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: source.appendingPathComponent("fixtures.json")))
        let promptData = try Data(contentsOf: source.appendingPathComponent("policy.txt"))
        let prompt = try XCTUnwrap(String(data: promptData, encoding: .utf8))
        guard fixtures.count == 10 else { XCTFail("Expected ten public seed fixtures"); return }
        try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)
        let output = outputDirectory.appendingPathComponent("\(cloud ? "cloud" : "local")-\(UUID().uuidString).json")
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 15
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        let endpoint = cloud ? "https://ollama.com/v1" : "http://127.0.0.1:11434"
        var rows: [[String: Any]] = []
        var report: [String: Any] = ["schemaVersion": 1, "provider": cloud ? "OpenAI-compatible Ollama Cloud" : "Ollama Native loopback",
            "endpoint": endpoint, "model": model, "promptSHA256": SHA256.hash(data: promptData).map { String(format: "%02x", $0) }.joined(),
            "startedAt": ISO8601DateFormatter().string(from: Date()), "os": ProcessInfo.processInfo.operatingSystemVersionString,
            "scope": "Supplementary public text cleanup smoke; manual meaning review required; not A00 or selected-candidate success",
            "context": "nil", "vocabulary": [String](), "reasoningPolicy": cloud ? "provider defaults; portable service has no explicit toggle" : "disabled when supported",
            "resourceTimeoutSecondsPerRequest": 15, "runStatus": "pendingDiscovery"]
        func save() throws {
            report["samples"] = rows
            try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]).write(to: output, options: .atomic)
        }
        try save()
        do {
            if cloud {
                let available = try await OpenAICompatiblePostProcessingService.availability(
                    baseURL: endpoint, apiKey: Settings.shared.openAICompatibleAPIKey, selectedModel: model, session: session)
                report["selectedModelListed"] = available.selectedModelIsAvailable
                // A manually configured model may be a valid unlisted alias.
                // Replay exactly the saved ID; generation establishes acceptance.
                report["modelDigest"] = "unavailable from portable discovery; mutable cloud ID"
            } else {
                // Metadata only, no pull/download endpoint. Persist only selected scalar fields.
                let (tagsData, tagsResponse) = try await session.data(from: URL(string: endpoint + "/api/tags")!)
                guard (tagsResponse as? HTTPURLResponse)?.statusCode == 200 else { throw BaselineError.metadataUnavailable }
                let tags = try JSONSerialization.jsonObject(with: tagsData) as? [String: Any]
                let models = tags?["models"] as? [[String: Any]] ?? []
                guard let selected = models.first(where: { ($0["name"] as? String) == model }) else { throw BaselineError.modelUnavailable }
                var showRequest = URLRequest(url: URL(string: endpoint + "/api/show")!)
                showRequest.httpMethod = "POST"
                showRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
                showRequest.httpBody = try JSONSerialization.data(withJSONObject: ["model": model])
                let (showData, showResponse) = try await session.data(for: showRequest)
                guard (showResponse as? HTTPURLResponse)?.statusCode == 200 else { throw BaselineError.metadataUnavailable }
                let show = try JSONSerialization.jsonObject(with: showData) as? [String: Any] ?? [:]
                // A loopback server may proxy cloud models. Refuse declared remote routing.
                guard selected["remote_host"] == nil, selected["remote_model"] == nil,
                      show["remote_host"] == nil, show["remote_model"] == nil else { throw BaselineError.modelUnavailable }
                report["capabilities"] = show["capabilities"] as? [String] ?? []
                report["routingObservation"] = "loopback; no remote_host/remote_model declared; network isolation not independently verified"
                report["modelDigest"] = selected["digest"] as? String ?? "unavailable"
                if let details = selected["details"] as? [String: Any] {
                    report["parameterSize"] = details["parameter_size"] as? String ?? "unavailable"
                    report["quantization"] = details["quantization_level"] as? String ?? "unavailable"
                }
            }
        } catch {
            report["runStatus"] = "discoveryFailed"
            report["failureCategory"] = category(error)
            try save()
            XCTFail("Provider discovery failed; sanitized category in artifact")
            return
        }
        report["runStatus"] = "running"
        var failures = 0
        for fixture in fixtures {
            let started = ContinuousClock.now
            rows.append(["id": fixture.id, "input": fixture.input, "status": "pending"])
            try save()
            do {
                let answer: String
                if cloud {
                    answer = try await OpenAICompatiblePostProcessingService.process(text: fixture.input,
                        baseURL: endpoint, model: model, apiKey: Settings.shared.openAICompatibleAPIKey,
                        prompt: prompt, vocabulary: [], context: nil, session: session)
                } else {
                    answer = try await OllamaPostProcessingService.process(text: fixture.input,
                        baseURL: endpoint, model: model, reasoningEnabled: false,
                        prompt: prompt, vocabulary: [], context: nil, session: session)
                }
                rows[rows.count - 1]["output"] = answer
                rows[rows.count - 1]["status"] = answer.isEmpty ? "empty" : "completedNeedsManualReview"
                if answer.isEmpty { failures += 1 }
            } catch {
                failures += 1
                rows[rows.count - 1]["status"] = "failed"
                rows[rows.count - 1]["failureCategory"] = category(error)
            }
            let duration = started.duration(to: .now).components
            rows[rows.count - 1]["elapsedMilliseconds"] = Double(duration.seconds) * 1_000 + Double(duration.attoseconds) / 1e15
            report["failureCount"] = failures
            try save()
        }
        report["runStatus"] = "completedNeedsManualReview"
        report["completedAt"] = ISO8601DateFormatter().string(from: Date())
        try save()
        XCTAssertEqual(failures, 0, "Provider attempt failed; review sanitized artifact")
    }

    private enum BaselineError: Error { case modelUnavailable, metadataUnavailable }
    private func category(_ error: Error) -> String {
        if let error = error as? URLError { return error.code == .timedOut ? "timeout" : "transport" }
        if error is CancellationError { return "cancelled" }
        if error is BaselineError { return "modelOrMetadataUnavailable" }
        if error is CleanupResponseError { return "invalidOrIncompleteCleanup" }
        if error is OllamaPostProcessingService.ServiceError { return "ollamaProviderError" }
        if error is OpenAICompatiblePostProcessingService.ServiceError { return "compatibleProviderError" }
        return "otherFailure"
    }
}
