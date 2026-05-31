# TECHMANIA — Architecture & Performance Notes (Dubita fork)

> **Method & status:** This document is from **static code analysis** of the
> `master` source (Unity `6000.3.9f1`). Runtime **Profiler baselines are not yet
> captured** — that requires a playable build, which needs FMOD for Unity +
> StreamingAssets (see [SETUP](#setup-state) below). Treat performance findings as
> **hypotheses to validate with the Profiler**, not measured results. Companion
> file: [`PERFORMANCE-AND-FEATURES-BACKLOG.md`](PERFORMANCE-AND-FEATURES-BACKLOG.md).

## Tech stack

- **Engine:** Unity `6000.3.9f1` (Unity 6.3). UI is **UI Toolkit / UIElements**
  (`VisualElement`, `VisualTreeAsset`) — *including the gameplay playfield*, not
  sprites/meshes.
- **Audio:** **FMOD Core** (not Unity audio), wrapped in `Assets/Scripts/Fmod/`.
  ASIO supported on Windows for low latency.
- **Scripting/extensibility:** **Lua via MoonSharp** ("Theme API"). Themes are
  Lua + UXML/USS shipped as AssetBundles.
- **Serialization:** JSON (`track.tech`, `setlist.tech`, skins, options).
- Dependencies vendored under `Assets/`: BetterStreamingAssets, SharpZipLib
  (`Zip Library`), NReco CSV (`CSV Reader`), StandaloneFileBrowser.

## High-level flow

```
Startup.Start()                       (Main.unity)
  ├─ FmodManager.Initialize(bufferSize, numBuffers)   ← audio latency knob
  ├─ Options / Statistics / L10n / Records / Discord
  └─ BootScreen.StartBooting()
        └─ GlobalResourceLoader: skins, theme (AssetBundle), track/setlist lists
              └─ Theme (Lua) drives all menus & navigation
                    └─ GameController  ← the gameplay engine
```

The **theme (Lua)** owns menus/navigation and calls into C# via the Theme API
(`Assets/Scripts/Theme API/`, entry `Techmania.cs`). Gameplay is a C# engine
(`Components/Main Scene/Game/`) exposed to Lua through `GameSetup`/`GameState`.

## The gameplay engine

`GameController` ([Components/Main Scene/Game/GameController.cs](TECHMANIA/Assets/Scripts/Components/Main%20Scene/Game/GameController.cs))
is the spine. `LoadSequence()` (coroutine) loads everything for a pattern, then
`Update()` ticks the subsystems every frame while `Ongoing`:

```
GameController.Update()  →  timer.Update()
                            bg.Update()
                            layout.Update(scan)       (GameLayout)
                            noteManager.Update()      (NoteManager → NoteElements.UpdateTime)
                            input.Update()            (GameInputManager)
                            inputFeedback.Update()
                            scoreKeeper.UpdateFever()
                            vfxAndComboText.Update()
```

| Subsystem | File | Role |
|---|---|---|
| Timer | `Game/GameTimer.cs` | Drives time/pulse/beat/scan |
| Notes (data + state) | `Game/NoteManager.cs`, `Game/NoteElements.cs` (+ `NoteElements Subclasses/`) | Spawn, position, per-frame visual update |
| Layout | `Game/GameLayout.cs` | Scanlines, countdowns, lane geometry, screen↔lane mapping |
| Input | `Game/GameInputManager.cs` | Touch/KM/Keys handling, judgement |
| Audio (keysounds) | `Game/KeysoundPlayer.cs`, `Game/AudioManager.cs`, `Fmod/*` | Playback via FMOD |
| VFX/combo | `Game/VfxAndComboText.cs` | Hit VFX + combo text |
| Scoring | `Game/ScoreKeeper.cs`, `Game/SetlistScoreKeeper.cs` | Score/HP/fever |

## Rendering model (the central FPS fact)

Every note is a **UI Toolkit `VisualElement`** instantiated from a per-type
`VisualTreeAsset`. **All notes for a pattern are instantiated up front** in
`NoteManager.Prepare()` (no streaming); state toggles (Inactive→Prepare→Active→
Resolved) control visibility. `ObjectPool`/`ObjectPoolManager` exist but are
explicitly **"Unused at the moment"** — there is no runtime note/VFX pooling.

Positioning uses **inline styles** (`element.style.left/top` in `%`). Per frame,
the loop writes many UI Toolkit style properties:

- `NoteElements.UpdateTime()` (for notes in **±2 scans**, each frame): `hitbox.style.opacity`,
  `noteImage` sprite via `UpdateSprites()`, plus fever/approach overlays set
  `style.backgroundImage = new StyleBackground(...)` and `style.opacity`.
- `GameLayout.Update()` (every scanline, **every frame**): `style.backgroundImage`,
  `style.left`, `style.opacity` + countdown backgrounds — written unconditionally
  even when unchanged.
- `VfxAndComboText` sets `style.backgroundImage = new StyleBackground(...)` per
  frame for judgement text, combo digits, and VFX layers.

Each style write marks the element dirty → UI Toolkit re-resolves styles and
repaints. This per-frame style churn (and `new StyleBackground` allocations) is
the prime **FPS** suspect at high note density. → backlog **P1, P6, P7**.

## Timing & sync model (central latency fact)

`GameTimer` derives `baseTime` from a **`System.Diagnostics.Stopwatch`** (system
wall-clock): `baseTime = stopwatch.Elapsed * speed + initialTime`. Audio plays on
**FMOD's independent clock**. There is **no sync of visuals to the audio DSP
clock** — audio/visual alignment relies entirely on the user's manual
calibration: `Options.touchOffsetMs` / `keyboardMouseOffsetMs` (applied as
`gameTime = baseTime − offset`). A separate `…LatencyMs` compensates input timing
(`GameInputManager.LatencyForNote`). → backlog **P3, P4**.

## Input model

Uses the **legacy `UnityEngine.Input`**, polled once per `GameInputManager.Update()`
(i.e., once per frame). Input timestamp granularity therefore equals frame time —
**higher FPS directly tightens timing precision**. Touch/mouse hits use a custom
raycast over notes in ±2 scans (`Raycast()` + `VisualElementTransform`). → ties
**FPS** work to **input latency**; see backlog **P5** and the Input System note.

## Audio model

`FmodManager` ([Fmod/FmodManager.cs](TECHMANIA/Assets/Scripts/Fmod/FmodManager.cs))
recreates an FMOD **Core** system and calls `setDSPBufferSize(bufferSize, numBuffers)`
— the **audio-latency knob**. Defaults (`Options`): `audioBufferSize = 1024`,
`numAudioBuffers = 4` (~21 ms/buffer at 48 kHz), `useAsio = false`. Channel groups:
Master/Music/Keysound/SFX. `system.update()` is pumped each frame. → backlog **P3**.

## Loading model

- **Startup track/setlist scan:** runs on a **`BackgroundWorker` thread** (good) —
  enumerates dirs, extracts `.zip` tracks (SharpZipLib), loads + `Minimize`s every
  `track.tech`. Main thread polls via `yield return null`.
- **Skins:** loaded **sequentially**, one sprite-sheet image at a time
  (`GlobalResourceLoader.LoadSkin` → `ResourceLoader.LoadImage` + `WaitUntil` +
  `GenerateSprites`). VSync is temporarily disabled during loads.
- **Theme:** `AssetBundle.LoadFromFileAsync`, iterating all assets.
- **Per-pattern keysounds (hot path):** `ResourceLoader.InnerCacheAudioResources`
  loads each file **sequentially** via `UnityWebRequestMultimedia.GetAudioClip`
  → `DownloadHandlerAudioClip` → `FmodManager.CreateSoundFromAudioClip` (full PCM
  **marshal copy**) → `clip.UnloadAudioData()`. This round trip is CPU- and
  memory-heavy and dominates load for keysound-rich charts. → backlog **P2, P8**.

## Extensibility (for features)

Features can often be added **without touching the C# core** by extending the
**Theme API** (`Assets/Scripts/Theme API/`) and shipping Lua/UXML. `Techmania.cs`
is the Lua-facing root; `GameState`/`GameSetup` expose gameplay; `VisualElementWrap`
wraps UI Toolkit for Lua. A **custom Ruleset** system already exists
(`Serializable/Ruleset.cs`), as do **Records** (`Serializable/Records.cs`) and
**Modifiers**. See backlog "Feature candidates".

## <a name="setup-state"></a>Setup state (this machine)

- ✅ Cloned: `origin` = `Dubita/techmania`, `upstream` = `techmania-team/techmania`.
- ✅ Unity `6000.3.9f1` at `D:\Programmi\Unity\6000.3.9f1\Editor\Unity.exe`.
- ✅ Node.js LTS installing (needs a one-time Windows UAC approval) — for Unity-MCP.
- ⛔ **FMOD for Unity** not yet imported — **required to build/run** (acquire from
  fmod.com; not redistributable in-repo).
- ⛔ `TECHMANIA/Assets/StreamingAssets` empty — populate from an official release's
  `Skins_and_Tracks.zip` for default skins/tracks.

## How to capture baselines (next step, once buildable)

1. Import FMOD + StreamingAssets, open `Main.unity`, enter Play (or build Windows).
2. **Unity Profiler** (Window ▸ Analysis ▸ Profiler): record a dense pattern.
   - FPS / frame time; **UIElements** "Update" + "Repaint" markers; GC Alloc/frame.
3. **Startup**: time cold launch + track-list scan + one pattern load (watch the
   keysound caching stage).
4. **Latency**: use the in-game calibration; note `offsetMs`/buffer values needed
   for good feel as the audio-latency reference.

Record numbers here so future changes can be compared against them.
