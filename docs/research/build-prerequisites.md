# A00 build prerequisites

Observed 2026-10-19. This is a prerequisite audit, not a successful build report. No dependencies, binary frameworks or model weights were installed, and no upstream scripts were executed. The imported application is being prepared separately.

## Result

The installed toolchain is sufficient to attempt the pinned application's build, but **an offline build is currently blocked by missing package checkouts and binary artifacts**. Shell access to GitHub also fails DNS resolution in this execution environment. This is an environment/dependency-access blocker, not evidence that the application architecture or source fails to build.

Do not remove Sparkle or llama.swift to turn this into an apparent baseline success. Preserve the original dependency pins and package composition. Disabling inherited updater startup/feed is required for independent app operation, but it does not eliminate Sparkle's compile/link dependency.

## Verified local toolchain

| Item | Observed value |
| --- | --- |
| Selected developer directory | `/Applications/Xcode.app/Contents/Developer` |
| Xcode | 27.1, build `27A9275` |
| Swift | Apple Swift 6.4, `swiftlang-6.4.0.34.1`, clang `2100.3.34.1` |
| Compiler target | `arm64-apple-macosx27.0.0` |
| macOS SDK | 27.0 |

Commands executed successfully: `xcode-select -p`, `xcodebuild -version`, `swift --version`, and `xcrun --sdk macosx --show-sdk-version`. Xcode emitted fs-event/cache-path warnings in the restricted environment while returning its version/SDK successfully; these warnings do not establish a build failure. Use workspace-local build/package caches for subsequent attempts where possible. Signing identities, notarization credentials and real-device permission grants were not audited here.

## Exact pinned dependencies

The source of package pins is the [inspected application lockfile](../../research/upstream/mac-dictate-anywhere/Dictate%20Anywhere.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved), from app commit `e420448de6e2099d33e050a4b419722032fd7519`.

| Package | Version | Commit |
| --- | --- | --- |
| FluidAudio | 0.17.5 | `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7` |
| llama.swift | 2.10549.0 | `716419d4d7aa542fce301e809cde7234c68ddbc6` |
| Sparkle | 2.9.6 | `ac2def288cbff5cfc7df3ffef6abdf45b72bcb0a` |

The general speech research snapshot at FluidAudio 0.17.7 is not the baseline dependency. Do not substitute it while resolving these prerequisites.

Pinned manifests were fetched read-only using the GitHub connector. They establish the following additional release downloads, beyond the package source trees. Checksums below are **declared upstream manifest values**; the archives have not been downloaded or independently verified during this audit.

### FluidAudio: NemoTextProcessing

[Pinned Swift 6.2 manifest](https://github.com/FluidInference/FluidAudio/blob/0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7/Package@swift-6.2.swift)

- Artifact: [NemoTextProcessing.xcframework.zip, v0.3.1](https://github.com/FluidInference/text-processing-rs/releases/download/v0.3.1/NemoTextProcessing.xcframework.zip)
- SHA-256: `5fa8c10d4ec26c1bb2413125f351a7222a4c68a23b74476680fbada7e26fc6aa`
- The Swift 6.2 manifest enables its `NemoTextProcessing` trait by default. Its declared external Swift package dependency list is empty, but this binary target is still a download requirement for the baseline composition.
- Opting out of the normalization trait is a possible later ASR-only packaging choice; it changes package composition and is not a substitute for reproducing the baseline.

### llama.swift: llama.cpp

[Pinned manifest](https://github.com/mattt/llama.swift/blob/716419d4d7aa542fce301e809cde7234c68ddbc6/Package.swift)

- Artifact: [llama-b10549-xcframework.zip](https://github.com/ggml-org/llama.cpp/releases/download/b10549/llama-b10549-xcframework.zip)
- SHA-256: `636709d4a7b49ac7f2ed6dc8a4de3f278c257cac7248fe40d6a513f671e4bff4`
- The `LlamaSwift` source target depends on the `llama-cpp` binary target. It declares no additional Swift package dependency.

### Sparkle

[Pinned manifest](https://github.com/sparkle-project/Sparkle/blob/ac2def288cbff5cfc7df3ffef6abdf45b72bcb0a/Package.swift)

- Artifact: [Sparkle-for-Swift-Package-Manager.zip, 2.9.6](https://github.com/sparkle-project/Sparkle/releases/download/2.9.6/Sparkle-for-Swift-Package-Manager.zip)
- SHA-256: `8d5fb41d960b43f4a68aa14126bf62b098544ec8d191cdcc73eb14e63a8e7606`
- Its SwiftPM product is a binary target. Source retrieval alone does not supply that framework.

Machine-readable URLs, pins and checksums: [dependency-artifacts.json](../../research/upstream/dependency-artifacts.json).

## Cache assessment

Inspected these standard locations and matching package/artifact filenames under `~/Documents/ai_projects`:

- `<HOME>/Library/Developer/Xcode/DerivedData`
- `<HOME>/Library/Caches/org.swift.swiftpm`
- `<HOME>/Library/org.swift.swiftpm`
- `<HOME>/.swiftpm` (configuration/cache/security symlinks)
- `<OTHER_PROJECT>/build/SourcePackages`

No FluidAudio, llama.swift or Sparkle checkout or matching binary archive/framework was found in the inspected locations. The only discovered pre-existing `SourcePackages/workspace-state.json` files describe unrelated filesystem `SkyKit`; their artifact arrays are empty. The inspected DerivedData `SourcePackages` checkout/repository/artifact directories are empty. The global SwiftPM cache has manifest databases but no artifact/repository directory or package metadata files. This is a scoped cache inspection, not a search of every possible custom filesystem location.

## Reproducible next step

1. Complete the pinned application import and discover its actual schemes/build settings; retain its original lockfile. Root-owned baseline scripts/report will record the executable build commands.
2. Resolve the existing pinned packages once from an execution environment with GitHub access, using a workspace-local package directory and the lockfile's exact versions. This must fetch both source repositories and the binary ZIPs above; GitHub release redirects also require functioning network access.
3. Verify package checkout commits and archive SHA-256 values against the manifest. Record the populated cache location and resolution output. Do not silently advance versions or use the 0.17.7 research snapshot as a replacement dependency.
4. Build with automatic package updates disabled against that populated cache, then run relevant inherited tests. An offline repeat build can establish reproducibility only after the dependency population succeeds.
5. Treat model installation and microphone/paste tests as separate prerequisites. Compiled frameworks do not include the required downloaded Parakeet model bundle.

If network resolution cannot run here, use network-enabled Xcode/shell outside this restricted execution environment, or transfer verified pinned checkouts and binary artifacts from another machine into a deliberate workspace-local cache. A source-only GitHub connector import does not satisfy the binary requirements. Building those frameworks from upstream source would be a separate substantial packaging effort and would not reproduce the exact prebuilt baseline without additional verification.

Current status: **toolchain verified; dependency resolution and first build pending**. No compile, test, signing, model, or runtime success is implied by this audit.
