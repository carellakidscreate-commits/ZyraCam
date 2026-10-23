# 2.9.0 verification and root causes

Implemented, reviewed and compiled against installed game assemblies. Unity checks use real Unity transforms, rendering, imported model and extracted production methods, with adapters for game/XR/Photon entry points. **No live Gorilla Tag, physical headset, map traversal or multiplayer test was performed.**

## Causes and changes

1. A: 2.8 CameraAGesture returned Show only on Pressed and ignored Held/Released; UpdatePlacement immediately froze the recalled tablet. Restored the existing 0.3-second tap/hold state machine. Actual model visibility determines hide/recall. A hold switches ownership to Selfie once; LateUpdate and late-XR callbacks sample the HMD directly. Release freezes the last pose without hiding. No repeated resource creation, preset save or settings changes. Files: IsaiahCameraPlugin.cs and CameraMotion.cs.
2. Screen: SyncPhysicalPresentation copied the capture's optical rotation to the model. With a lens aimed toward the player, the back-mounted screen pointed away. TP/Follow now orient only the physical model's screen toward the owner and preserve its center, capture rotation/position, follow target, damping and offsets. Handheld/FP/grip ownership remain separate. File: CameraMotion.cs.
3. Recurring work: PrepareCaptureCulling searched globally for two game component types every two seconds; RefreshEnvironmentControllers repeated a global search and forced all native ambience updates together. Both now discover once at startup/scene load and register late activations through native OnEnable Harmony postfixes. Destroyed entries are pruned; local cosmetics retain their existing refresh. Native weather controllers keep their normal slicer. If a future game version lacks culling hooks, the existing polling compatibility fallback remains with a warning. Environment failure still follows the existing N/A/restoration behavior. Files: CameraCaptureCulling.cs, CameraEnvironment.cs.
4. Repeated time matching lowercased preset names in nested loops, including per-frame calls. Cached matching detects replaced arrays and in-place changes, and preserves all results. Removed an unused per-frame uppercase string. Environment scope is a value type; desktop camera discovery reuses a list. Files: CameraLocalTime.cs, CameraEnvironment.cs, IsaiahCameraPlugin.cs.

These are verified sources of recurring work/allocations. Without a game profiler trace, they are **not proof of the sole cause of the user's stutter**, nor proof that all live-game spikes are gone.

## Actual checks

- Release and independent clean-directory builds: zero warnings/errors, byte-identical DLL. Single-DLL resource loader ran three load/unload cycles in Unity; exact bundle hash and 190 original meshes retained, shaders supported, original click PCM decoded.
- A: 2,012 compiled gesture samples; 4,800 synthetic fast head-follow frames across four starting modes and both visibility states; immediate center/capture alignment, one entry/audio activation, unchanged identities/settings/presets/colors, tap hides, held release parks. 400 additional hide/recall cycles preserve the feed. 1,200 two-hand grip/late-XR samples and 160 instant-flip transitions pass.
- Orientation/preview: 800 automatic physical poses leave optics unchanged and screen facing owner. TP/Follow actual screen pixels update cyan/magenta from four owner directions (64 pixel samples). 36 rolled front/selfie poses, including upside down, retain correct nonmirrored preview.
- Culling: late lifecycle registration, duplicate deduplication, destroyed-entry removal, fake HMD-bound geometry and trigger-hidden geometry render correctly and restore native state. Equipped hat/face hiding preserves base mouth/body and unrelated players.
- Environment: 100 periodic checks cause no extra forced ambience ticks; late activation refreshes only that controller. 60 time/rain transitions, native clock/weather restoration and exception/unpatch recovery pass.
- Time cache: 1,100 selection/mutation comparisons match 2.8 results. Actual compiled DLL benchmark, 100,000 repeated lookups: 2.8 = 197.007 ms / 208,000,000 allocated bytes; 2.9 cached = 10.813 ms / 0 bytes. This is a .NET CPU microbenchmark, **not gameplay FPS, Unity frame-time or GPU measurement**.
- Regression: 65 selectable controls, all 36 RGB directions and aliases, 13 color groups, independent lens holder, FOV/clipping/smoothness, More/Colors, roll lock, FP position/rotation stabilization, all confirmations, desktop output recovery and centralized audio dispatch pass synthetic checks. Six slots plus selected slot survive real BepInEx test-config reloads. User config was not overwritten.
- NETWORK: source unchanged; compiled full/compact packet codecs and malformed inputs pass. Unity in-memory old/new-peer, appearance, heartbeat and OFF cleanup tests pass; no duplicate bodies. Existing UI/network benchmarks rerun. No real Photon traffic or live lobby hop was executed.
## Developer In-Game Testing

In addition to the automated tests documented above, ZyraCam was tested directly inside Gorilla Tag by the developer.

- All documented camera features were tested in-game.
- The developer reports that all tested features worked as intended.
- Camera controls and modes were checked during gameplay.

These in-game tests were performed separately from the automated verification process. The results are developer-reported and have not been independently verified.
