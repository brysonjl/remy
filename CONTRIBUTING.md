# Contributing to Remy

Remy is an independent adaptation of Dictate Anywhere. Start with the
[README](README.md), [source guide](app/README.md), and
[architecture](docs/architecture.md). Public app distribution is still being
prepared; a successful local build does not establish release compatibility.

## Propose a change

Use an issue to describe the problem and intended behavior before undertaking a
large feature or architectural change. Small fixes can go directly to a pull
request. Keep changes focused and preserve upstream attribution and notices.
Describe the user-visible result, relevant tradeoffs, and how you verified it.

Use synthetic examples in reports. Do not attach API keys, signing files, personal
recordings, transcripts, page titles/URLs, clipboard contents, or unreviewed logs.
Report vulnerabilities using [SECURITY.md](SECURITY.md).

## Build and verify

The currently verified workflow targets Apple silicon and Xcode 27.2 (27B52).
Earlier validation used Xcode 27.1; the README records the current supported
toolchain and successful local checks. A deployment target of macOS 14 is not evidence
that every intervening OS release has been tested. Follow the README for Xcode
setup, dependency resolution, and optional local signing. From the repository root:

```sh
app/scripts/remy-build.sh resolve
app/scripts/remy-build.sh build
app/scripts/remy-build.sh build Release
app/scripts/remy-build.sh test -parallel-testing-enabled NO
validation/run-tests.sh
```

Ordinary build/test commands disable signing and use an isolated test identity.
Do not run interactive app tests against someone else's active dictation session.
Model, microphone, live-provider, and destination-app checks are separate opt-ins;
report skipped or unavailable checks honestly. See the
[validation guide](validation/README.md) and
[acceptance criteria](docs/evaluation/acceptance.md).

For code changes, run the checks affected by the change and add regression
coverage when it demonstrates a meaningful failure. Documentation-only changes
need link/content review rather than a full app build. Do not update package pins
or model manifests incidentally; an intentional update must review integrity,
compatibility, notices, and the documented validation expectations together.

## Review and licensing

Include a concise description, verification results, and known limitations in the
pull request. Maintainers may request scope changes or defer work outside the
published roadmap. No response-time or compatibility guarantee is implied.

Submit only material you have permission to contribute. Unless you explicitly
state otherwise and agree an alternative with maintainers before submission,
your original contributions are offered under the repository's MIT license.
Third-party material retains its own terms and must be identified with provenance
and notices; see [third-party notices](THIRD_PARTY_NOTICES.md). The
[code of conduct](CODE_OF_CONDUCT.md) applies to project discussions.
