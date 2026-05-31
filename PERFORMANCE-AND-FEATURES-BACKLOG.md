# TECHMANIA Fork — Performance & Feature Backlog

Ranked candidates from static analysis (see
[`ARCHITECTURE-NOTES.md`](ARCHITECTURE-NOTES.md)). **Validate with the Profiler
before/after.** Impact/effort/risk are estimates. Areas: **[FPS]** gameplay frame
rate · **[LOAD]** load/startup · **[LAT]** input/audio latency.

Each item is meant to become **its own branch + change** in a later session.

## Performance — recommended order

### P1 — Kill redundant per-frame UI Toolkit style writes  **[FPS]**  ★ top pick
Loop writes `style.backgroundImage`/`opacity`/`left` every frame on every active
note, scanline, countdown, and VFX element — even when the value is unchanged.
Each write dirties UIElements style-resolution + repaint.
- **Do:** cache last-applied sprite/opacity/length per element; only assign on
  change. Avoid `new StyleBackground(...)` when the sprite is identical. Skip
  `hitbox.style.opacity` writes when `showHitbox` is false and already 0.
- **Where:** `Game/NoteElements.cs` (`UpdateTime`, `UpdateFeverOverlay`,
  `UpdateApproachOverlay`, `HitboxMatchNoteImageAlpha`), `Game/GameLayout.cs`
  (`Update`), `Game/VfxAndComboText.cs`, note subclasses' `UpdateSprites`.
- **Impact:** High · **Effort:** Medium · **Risk:** Low–Med (verify visual parity).

### P2 — Load keysounds directly via FMOD `createSound` (drop the AudioClip round trip)  **[LOAD]**
Today each keysound is `UnityWebRequest → AudioClip → marshal PCM copy → FMOD →
UnloadAudioData`, **sequentially**.
- **Do:** decode natively with `system.createSound(path, …)` (optionally async /
  in parallel). Removes the marshal copy and AudioClip allocation.
- **Where:** `Components/ResourceLoader.cs` (`InnerCacheAudioResources`,
  `GetSoundFromWebRequest`, `CreateSoundFromAudioClip`), `Fmod/FmodManager.cs`.
- **Impact:** High (load time + peak memory) · **Effort:** Medium · **Risk:** Med
  (format coverage, URI/Android StreamingAssets paths).

### P3 — Lower-latency audio defaults + ASIO preset  **[LAT]**
Default buffer is `1024 × 4`; ASIO off.
- **Do:** add a "low latency" preset (e.g. `256–512 × 2`) with safe fallback on
  underrun, surface it clearly, and verify the ASIO path end-to-end. Keep the user
  override (changing buffer already triggers `ResourceLoader.forceReload`).
- **Where:** `Serializable/Options.cs` (`audioBufferSize`, `numAudioBuffers`,
  `useAsio`, `ApplyAsio`), `Fmod/FmodManager.cs` (`Initialize`, `useASIO`).
- **Impact:** High (audible latency) · **Effort:** Low–Med · **Risk:** Med
  (underruns on weak hardware — must stay user-tunable).

### P4 — Drive the game clock from the FMOD audio position (not Stopwatch)  **[LAT]** · larger
Replace/augment the `Stopwatch` base time with the backing track's FMOD playback
position so audio↔visual stay locked regardless of buffer latency or frame hitches.
- **Do:** when a backing track is playing, sample FMOD channel position / DSP clock
  with jitter smoothing; fall back to `Stopwatch` when no track.
- **Where:** `Game/GameTimer.cs`, `Game/GameController.cs` (`bg.backingTrack`),
  `Fmod/FmodChannelWrap.cs`.
- **Impact:** High (sync quality, less manual calibration) · **Effort:** High ·
  **Risk:** High (timing is core — needs careful testing). Flag as a project.

### P5 — Optional FPS cap + document FPS↔timing link  **[FPS]/[LAT]**
Desktop runs uncapped (vSync off, no `targetFrameRate`). Since input is frame-bound,
higher FPS tightens timing — but uncapped can waste GPU/cause thermals.
- **Do:** optional user FPS cap; document the tradeoff in options.
- **Where:** `Serializable/Options.cs` (`ApplyGraphicSettings`).
- **Impact:** Med · **Effort:** Low · **Risk:** Low.

### P6 — Trim per-frame GC allocations in the loop  **[FPS]**
`notesInScan[scan].ForEach(e => …)` allocates closures each frame; `new
StyleBackground(...)` per frame; LINQ `.Reverse()/.First()` on hot paths.
- **Do:** cache delegates / use indexed `for`; fold into P1's change-detection.
- **Where:** `Game/NoteManager.cs` (`Update`), `Game/NoteElements.cs`.
- **Impact:** Med (frame-time consistency) · **Effort:** Low–Med · **Risk:** Low.

### P7 — Virtualize/pool note elements by scan window  **[FPS]** · larger
All notes are realized as VisualElements up front (no pooling). For very dense
charts this inflates the live element count, memory, and repaint cost.
- **Do:** realize/recycle note elements only within a window around the current
  scan (reuse the now-unused `ObjectPool` concept for UIElements).
- **Where:** `Game/NoteManager.cs`, `Game/GameLayout.cs`, `Game/NoteElements.cs`.
- **Impact:** High for dense charts · **Effort:** High · **Risk:** Med–High
  (interacts with hit detection + layout). Do after P1.

### P8 — Parallelize skin/sprite-sheet image loading  **[LOAD]**
Skins load one image at a time.
- **Do:** read raw bytes on worker threads, then create `Texture2D` on the main
  thread (texture creation is main-thread only); pipeline the two.
- **Where:** `Components/Main Scene/GlobalResourceLoader.cs` (`LoadSkin`),
  `Components/ResourceLoader.cs` (`LoadImage`).
- **Impact:** Med · **Effort:** Medium · **Risk:** Med.

### Exploratory — migrate gameplay input to the Input System  **[LAT]**
Legacy `Input` is frame-polled. Unity's Input System can timestamp events
sub-frame, decoupling timing precision from FPS. Large change; prototype first.
- **Where:** `Game/GameInputManager.cs`.

## Feature candidates (to refine with you)

The Theme API (Lua) + existing systems let many features land with little/no core
change. Candidates — **not yet prioritized; pick a direction:**

- **Gameplay/feel:** more note-opacity/scroll modifiers; extra control schemes;
  per-hand/finer calibration; auto-calibration (pairs well with P4).
- **Replays / ghost / practice tooling** (input is centralized in `GameInputManager`).
- **Scoring/rulesets:** new judgement or scoring modes via the existing custom
  **Ruleset** system (`Serializable/Ruleset.cs`).
- **Records/online:** the **Records** system (`Serializable/Records.cs`) could back
  local leaderboards or (bigger) online score sync.
- **Theming/skins:** new VFX/note/combo skins; theme-side UI features in Lua.
- **Accessibility:** colorblind-safe skins, reduced-motion mode, input remapping.

## Suggested first milestone

**P1 (+ P6)** as the first branch: highest FPS impact, contained, low risk, and it
*also* improves input timing precision (input is frame-bound). Capture Profiler
baselines first so the win is measurable.
