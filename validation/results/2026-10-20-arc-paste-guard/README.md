# Arc paste guard depth correction

The user reported normal Arc dictation producing the manual Command+V recovery
notification. Recent production diagnostics show repeated `ancestryDepthLimit`
and `captureRejected`, followed by `copiedOnly automatic=false targetProof=false
targetActive=true`. Rejection occurred in roughly 6–11 ms, well below the 200 ms
budget. Other captures in the same interval succeeded at depth 3. These logs do
not label the destination app, so attribution to Arc comes from the user report.

The guard previously checked only depths 0–11. A field with its window at depth
12 or deeper could never establish destination authority. The correction allows
up to 128 parent hops, including the exact boundary, while retaining the same
200 ms deadline and checking every ancestor's safety facts. Repeated elements
now reject immediately. Focused-field writing capability, protected/read-only/
disabled checks, exact process/launch/window/field revalidation, clipboard
ownership, cancellation, restoration policy and terminal newline handling remain.

Six new production-helper tests cover safe depth 24 and 128, exhausted depth,
unsafe ancestors beyond the old limit, unsafe windows, missing/wrong windows,
self/nonadjacent cycles, and expiration before/during parent and safety reads.
The signed isolated XCTest run passed **37 tests, zero failures**: 17 target
policy tests and 20 session integration tests. An independent agent reviewed the
production change and found no concrete issues.

Initial diagnostics were inspected live; a later export no longer returned the
pre-fix info-level records, so no pre-fix log artifact is claimed. The private
local `after-diagnostics.log` contains bounded reasons/depths only, with no field text,
titles, URLs, dictated text or clipboard content. Local test log:
`/tmp/alfred-arc-paste-tests.log`. Signed Release build log:
`/tmp/alfred-arc-paste-release.log`.

Live Arc automatic insertion was subsequently confirmed below. A separate read-only
Swift AX metadata probe lacked Accessibility authorization (`-25211`); no system
permission was changed. This does not affect Alfred's existing authorization.

## Local update

Signed Release build and `codesign --verify --deep --strict` passed. Alfred was
observed idle before quitting through its UI. The existing `alfred-local.sh`
prepare/install workflow staged and installed the repaired app at
`~/Applications/Alfred.app`; the previous installed signed app is retained at
`~/Applications/.alfred-local-backup.deDiIb/Alfred.app`. Prepare/install logs are
`/tmp/alfred-arc-paste-prepare.log` and `/tmp/alfred-arc-paste-install.log`.

Installed and built executables share SHA256
`68fc18aabdd64d2a0ceea6afec24976c8d9ff9d9c4d55bcead9d4d4e0c7bd317`.
The installed app was relaunched through native UI; Speech Model settings show
the existing Parakeet v2 selection, downloaded status and “Loads on first
dictation,” with no Accessibility permission banner.

## Live confirmation

The user retried the same Arc field and confirmed “Yes, automatic paste works.”
The private local `after-diagnostics.log` records the restarted production app
capturing a target at **depth 22**, followed by three successful exact-target
revalidations at depth 22. The initial capture took about 24 ms. This confirms
that the old depth cap was too small for the actual destination. Field text and
clipboard contents were not inspected; this is one successful user-confirmed
Arc case, not a complete destination matrix.

Publication note, 2026-11-05: the bounded diagnostic capture is retained privately
and omitted from the source snapshot. This curated report preserves the observed
result without distributing raw process logs.
