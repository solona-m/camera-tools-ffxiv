# Camera Tools for FFXIV

A free camera for FFXIV that presents itself to ReShade as an **IGCS camera tool**, so
accumulation depth-of-field add-ons can drive it.

The motivating case is Marty McFly's [iMMERSE Parallax Depth of
Field](https://guides.martysmods.com/shaders/immerseultimate/parallaxdof/), which renders
depth of field by jittering the camera and stacking frames. It requires an IGCS camera
tool to move the camera for it, and Otis_Inf builds those per-game — FFXIV is not among
them. That is the gap this fills. The same interface also enables Otis_Inf's
[IgcsConnector](https://github.com/FransBouma/IgcsConnector) IGCS DoF, panorama, and
multishot capture.

## How it works

ReShade add-ons discover a camera tool by walking the game process's loaded modules and
calling `GetProcAddress` for `IGCS_StartScreenshotSession`. There is no whitelist and no
signature check — any module exporting the interface is accepted.

A managed Dalamud assembly has no PE export table, so it can never be found that way.
Hence two pieces:

| Component | Language | Role |
|---|---|---|
| `src/native/IgcsBridge` | C++ | ~120 lines whose only job is to *be findable*. Exports the four `IGCS_*` entry points and forwards them to the plugin. |
| `src/CameraToolsXIV` | C# | The Dalamud plugin: owns the camera, implements the session semantics, publishes camera data, and draws the UI. |

### The interface

Reconstructed from IgcsConnector's BSD-licensed headers and confirmed against
`MartysMods_ParallaxDOF.addon64`'s export and string tables. This is an independent
implementation of that interface, not a copy of Otis_Inf's code.

**Exports the add-on calls on us:**

| Export | Signature |
|---|---|
| `IGCS_StartScreenshotSession` | `int(uint8_t type)` — `0` panorama, `1` multishot; returns `0` ok or `1`–`5` |
| `IGCS_MoveCameraPanorama` | `void(float stepAngle)` — radians, positive right |
| `IGCS_MoveCameraMultishot` | `void(float l, float u, float fovDeg, bool fromStart)` |
| `IGCS_EndScreenshotSession` | `void()` |

> The return type of `IGCS_StartScreenshotSession` is a 4-byte `int` on x64 and only
> narrows to `uint8_t` under IGCS's 32-bit build. Getting this wrong returns garbage.

**The buffer we publish into:** we scan for any module exporting
`connectFromCameraTools`, call it to allocate its 8 KB buffer, fetch that buffer with
`getDataFromCameraToolsBuffer`, and write an 84-byte `CameraToolsData` into it every
frame. Note the mixed units — FoV in **degrees**, Euler angles in **radians**.

### Two design choices worth knowing

**Orientation comes from the game's own view matrix.** The IGCS interface expects a
particular handedness and axis convention, and guessing at FFXIV's is the easiest way to
ship an orientation that is subtly wrong and only shows up as misaligned DoF frames.
Reading the matrix the game actually rendered with makes the question moot: whatever
convention it uses, the extracted basis vectors are correct in world space. See
[`ViewBasis`](src/CameraToolsXIV/Camera/ViewBasis.cs).

**The camera override runs inside a hook on `CameraBase::Update`.** The game recomputes
the camera transform every frame, so anything written beforehand is overwritten, and a
framework tick has no ordering guarantee against that update. Writing on the way out of
the vtable call is the only placement that reliably wins. See
[`CameraController`](src/CameraToolsXIV/Camera/CameraController.cs).

## Building

Requires the .NET 10 SDK, Visual Studio Build Tools with the C++ workload, and a Dalamud
dev install at `%AppData%\XIVLauncher\addon\Hooks\dev`.

```powershell
./build/build.ps1 -Deploy
```

Build the native shim first if building by hand — the plugin only packages
`IgcsBridge.dll` if it already exists on disk.

The build also runs `tests/IgcsBridgeHarness`, which exercises the add-on boundary
without needing the game: it registers callbacks the way the plugin does, then discovers
and calls the exports the way an add-on does. Marshalling mistakes there produce
plausible-looking garbage rather than a crash, so they are worth catching on the desktop.

## Status

Working: the IGCS export surface, add-on discovery and per-frame data publishing, the
free-camera override, and the screenshot-session semantics that depth of field drives.

Not yet built: keyboard/gamepad fly controls, camera roll, camera paths with
interpolated playback, presets, and rebindable hotkeys.

### Calibration

Before trusting a depth-of-field stack, confirm the published data is right. Install
`IgcsSourceTester.fx` from the IgcsConnector repository — it draws every `IGCS_*` uniform
on screen. Those values and the plugin's "Published to ReShade" panel should agree, and
should track as you move the camera.

## Limitations

- **Parallax DoF wants a paused world, and FFXIV cannot pause.** Group Pose freezes
  actors, but water, foliage, weather and particles keep animating and will ghost across
  a stack. Inherent to an MMO; manageable by shot selection, not fixable here.
- Disable V-Sync for frame-step synchronisation, per Marty's own guidance.
- The free camera is restricted to Group Pose by default. The setting to use it during
  normal play is deliberately opt-in: an untethered camera in the overworld sees through
  walls and terrain.
- Dalamud plugins are against FFXIV's Terms of Service. This is client-side visual
  tooling in the same category as Brio and Ktisis.

## Credits

The IGCS camera tool interface is the work of Frans 'Otis_Inf' Bouma
([IGCS](https://github.com/FransBouma/InjectableGenericCameraSystem),
[IgcsConnector](https://github.com/FransBouma/IgcsConnector), BSD licensed). Parallax
Depth of Field is by Pascal 'Marty McFly' Gilcher.
