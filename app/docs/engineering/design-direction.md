# Remy's service station

Implemented direction, 2026-10-19: a friendly robot tech waiter serves your words. Cream ceramic, a navy vest and teal lights give Remy a warm character within a precise native macOS console. This replaces the inherited copper/brown palette and editorial serif headings.

The shared design system uses warm cream surfaces in light appearance, deep navy in dark appearance, teal actions/status accents, and rounded system headings with system UI body lettering. The mascot's coral bowtie and amber trim are small character accents. Settings retain their existing page structure, controls and actions. The sidebar introduces “At your word.” and “SERVICE CONSOLE”; its footer clearly labels actual service status without claiming readiness or privacy that the runtime has not established.

`DesignTokens.swift` holds appearance-aware colors. `AccentColor` remains catalog-driven with light/dark teal variants. Cards and controls have restrained tactile shadows and rounded edges. Selection has a rail, icon, weight and accessibility trait as well as tint. Decorative art is hidden from VoiceOver; existing labels, keyboard behavior, focus and increased-contrast controls are retained. No animation is added, avoiding distraction and reduced-motion concerns. The character has an animated-film illustration style, not an animated asset.

For asset reuse terms and unresolved audio provenance, see
[asset provenance](../../../docs/engineering/asset-provenance.md).

## Original artwork provenance

Generated with the built-in `image_gen.imagegen` tool, transparent-background mode; no API fallback or credentials. The generated PNG
is committed at the portable asset path below; the original local generation
session is not required to build the app.

Copied without image editing into `app/Dictate Anywhere/Assets.xcassets/RemyWaiter.imageset/remy-waiter.png`; local review copy `.local/design/remy-waiter.png`. The project contains the real image, independently of its generator output location. This is an original generated mascot, not a licensed third-party character.

Final generation prompt:

> Use case: stylized-concept. Asset type: transparent PNG mascot for Remy, a native macOS dictation app. Create an original friendly cyberpunk robot tech waiter, polished high-end cartoon 3D caricature with animated-film charm, expressive glowing teal eyes in a deep navy face visor, warm cream ceramic head and rounded shell, compact deep navy waiter vest, tiny amber/coral bowtie, subtle teal service-status lights. The welcoming robot holds a small tray carrying one tiny luminous speech bubble as the visual metaphor for serving words. Three-quarter front view, full character entirely visible and centered with generous clear padding, charming oversized head, compact body, readable at small UI sizes, soft studio lighting, crisp dimensional materials. Transparent background with actual alpha, isolated cutout, no environment, no cast floor plane, no text, no letters, no watermark, no logos, no resemblance to a copyrighted character. Restrained cream/navy/teal with one small coral accent, approachable and capable rather than aggressive.

## Validation

Source parsing passed for all changed Swift files. Static sRGB contrast calculation found normal text pairs above 4.5:1 in both appearances: secondary/card 5.97 light / 7.49 dark, primary button label/fill 5.34 / 9.00, ink/card 14.06 / 12.65. Existing XCTest contrast gates remain; exact token assertions were updated to the intentional palette/font/radius specification, and a primary-button contrast gate replaces bundled-font registration now that the UI uses system fonts.

Full app compilation, XCTest and visual QA are performed by the integrating agent; source parsing and static contrast calculations alone do not establish runtime layout or rendering quality.

## App icon and menu-bar mark

At the user's subsequent request, the inherited waveform icon was replaced with a close-up of Remy's cream robot head, teal eyes and coral bowtie on a navy rounded tile. The existing full-body sidebar mascot remains the character reference. The menu bar uses an original monochrome robot-head/bowtie template drawn with CoreGraphics, with transparent visor cutouts so macOS can render it appropriately in either appearance.

The project-owned raster master is [../../Artwork/remy-app-icon.png](../../Artwork/remy-app-icon.png) (1254×1254 RGBA). `swift app/scripts/generate-app-icon.swift` from the workspace root resamples this master into all ten existing AppIcon slots (16–1024 px), and regenerates the 18/36/54 px menu templates. `--menu-only` regenerates just the template. The generator does not regenerate art, mutate the master, or require network access. Existing asset names and Xcode app-icon settings are preserved. The prior icons are retained locally under `.local/design/icon-before/`.

Generated using the built-in `image_gen.imagegen` tool, with the existing project mascot as the reference image and transparent-background mode; no CLI/API fallback. The generated raster master is
committed at the portable path above; the original local generation session is
not required to regenerate its sizes.

Final prompt:

> Use case: identity-preserve. Asset type: finished square macOS application icon for Remy. Reference image: the existing original Remy robot waiter mascot; preserve this character's cream ceramic head, dark navy visor, friendly teal crescent eyes, coral bowtie and warm animated-film charm. Create a new simplified close-up icon composition: the robot's oversized head centered nearly front-facing with a slight welcoming tilt, tiny upper vest and very clear coral bowtie below. Head and bowtie are the whole emblem; omit arms, legs, tray and speech bubble so it reads instantly at Dock size. Place it on a deep midnight navy rounded-square tile with a restrained teal edge glow and subtle dimensional bevel. Polished stylized 3D cartoon materials, warm cream highlights, confident clean silhouette, minimal surface detail, friendly and capable. Square 1024-by-1024 canvas. The rounded-square tile occupies 82 percent of the canvas, centered, with transparent outer padding and actual alpha outside it; all head/ears/bowtie stay entirely inside the tile. One finished icon only, no mockup, no labels, no text, no letters, no watermark, no sheet of variations. Avoid noisy circuitry, tiny details, extra antennae, intense bloom, detached parts, environment or large drop shadows. Keep the reference character identity and the cream/navy/teal/coral palette.

The tool returned a 1254px master; packaging resamples it to the asset catalog's exact dimensions. It remains a raster illustration with native alpha, while the tiny menu mark is independently reproducible geometry.

Validation: all ten app-icon slots and three menu-template scales generated successfully. The contact sheet `.local/design/icon-proof.png` was inspected on cream/navy surfaces at 16/32/64/128/256 px and menu 18/36 px: recognizable small silhouettes, no clipping or visible alpha fringe. Signed Debug preparation and Release builds passed (`icon-signed-prepare.log`, `icon-release.log` in the A00 results directory). The rebuilt stable app launched, and Finder Get Info displayed the new robot icon. Remy's light General page was visually inspected without visible clipping; System theme was restored afterward. The user later removed/re-added the exact stable app Accessibility entry; the permission banner disappeared and Ready to dictate was observed. The underlying cause of the earlier denial remains unproven.

## Recording service panel

The user's follow-up request extends the robot-waiter direction to the floating
recording panel: opaque navy/cream surfaces, teal waveform/status, a small
robot-head `MenuBarIcon`, a Listening header, warm status dot and rounded status
font. Panel sizing includes the header for preview-enabled and compact layouts.
The opaque treatment improves surface clarity; it is not claimed as the remedy
for the separate native visibility problem.

The user saw the whole panel only when Remy was active. Native diagnostics
showed four Chrome cases with native visibility, full alpha and in-display frames,
but outside the active Space and not occlusion-visible; the Remy-active external
display case was both on-Space and occlusion-visible. The focused runtime repair
uses a nonactivating `NSPanel` unable to become key/main, remaining visible on
deactivation and supporting all applications alongside the existing Spaces flags.
Main settings-window lookup excludes panels/floating/borderless windows.

The second focused run passed 43 tests with zero failures across AppDelegate,
appearance and overlay content/screen-session/native-window behavior. Real mounted
SwiftUI pixel coverage checks light/dark, preview on/off and hide/show reuse;
`.local/design/overlay-proof/` listening previews were visually reviewed in both
appearances. The first test compile attempted to set read-only
`accessibilityReduceMotion`; a disabled-animation transaction replaced that test
setup before the passing run. [Focused log](../../../validation/results/2026-10-19-a00/overlay-service-tests-r2.log).

Signed Debug preparation and Release builds for this repair passed
(`overlay-service-prepare.log`, `overlay-service-release.log`). Actual settings
were visually reviewed in Compact, Comfortable and Large modes without clipping,
then restored to Comfortable. The user confirmed the panel was visible over Chrome
and inserted text was correct; independent field inspection read “The teal waiter
keeps the final blue word.” Three latest native presentation rows from PID 67384
reported active-Space membership and occlusion visibility on the primary display
at x=710/y=6/width=380/height=177. The prior height of 154 became 177 to accommodate the
intentional header. [After-repair evidence](../../../validation/results/2026-10-19-a00/overlay-presentation-after.log).

This confirms the repair in one actual Chrome use case. The full Spaces/display
matrix, Arc/Ghostty/Codex and focus-change checks remain pending; A00 is open.
