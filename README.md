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

**The override lasts only as long as a stack.** Depth of field needs to own the camera
for the seconds it spends stepping around an aperture, and no longer. So arming the
camera changes nothing on its own — it just sets the `cameraEnabled` flag the add-on
reads — and the transform is taken over between `IGCS_StartScreenshotSession` and
`IGCS_EndScreenshotSession`, then handed straight back. You frame the shot with whatever
you already use (Group Pose, [Cammy](https://github.com/UnknownX7/Cammy),
[Brio](https://github.com/Etheirys/Brio)) and this stays out of the way.

Orientation is frozen alongside position for the duration of a stack. That matters: an
accumulation stack has to be a set of *parallel* translations. If the camera kept looking
at a fixed world point while stepping sideways it would toe in, and the shader's
focus-delta realignment assumes it did not.

## Installing

Add this to Dalamud's custom plugin repositories (`/xlsettings` → Experimental):

```
https://dl.solona.info/repo.json
```

Camera Tools is **testing-exclusive** until its first stable release, so enable "Get
plugin testing builds" in the same settings page or it will not appear in the list.

You also need [ReShade](https://reshade.me) 6.4 or newer **with add-on support**, plus a
depth-of-field add-on that speaks IGCS — iMMERSE Parallax DoF, or Otis_Inf's
[IgcsConnector](https://github.com/FransBouma/IgcsConnector). The plugin does nothing on
its own; it exists so those can drive the camera.

Type `/camtools` to check the connection. Two green lines mean it worked: the IGCS
exports are live, and an add-on has found them.

## Building

Requires the .NET 10 SDK, Visual Studio Build Tools with the C++ workload, and a Dalamud
dev install at `%AppData%\XIVLauncher\addon\Hooks\dev`. The MSVC toolset is detected
rather than pinned, so any recent Visual Studio works — pass `-PlatformToolset` to
override.

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
session-scoped camera override, and the screenshot-session semantics that depth of field
drives.

Confirmed in-game: Parallax DoF discovers the exports by module scan, connects, and drives
the camera through a session. The published basis checks out as orthonormal and
left-handed consistent (`right × up` equals `fwd` exactly).

Still unverified: a *completed* stack. Everything tested so far has been the setup
preview, not a full 1024-sample render.

Deliberately not built: fly controls. Parallax DoF needs us to *own* the camera position,
not to move it for you, and Cammy and Brio already do camera movement well. Still open,
if they turn out to be wanted: camera paths with interpolated playback, presets, and
rebindable hotkeys.

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
- The camera is only offered to add-ons in Group Pose by default. The setting to allow it
  during normal play is deliberately opt-in: a camera an add-on can reposition freely in
  the overworld sees through walls and terrain.
- Marty's rangefinder is logarithmic (`exp2(FOCUS_DELTA - 16)`), so values below about 10
  are indistinguishable from zero and read as the control doing nothing. It also
  multiplies with the blur radius, so neither can be zero.
- Dalamud plugins are against FFXIV's Terms of Service. This is client-side visual
  tooling in the same category as Brio and Ktisis.

## Credits

The IGCS camera tool interface is the work of Frans 'Otis_Inf' Bouma
([IGCS](https://github.com/FransBouma/InjectableGenericCameraSystem),
[IgcsConnector](https://github.com/FransBouma/IgcsConnector), BSD licensed). Parallax
Depth of Field is by Pascal 'Marty McFly' Gilcher.
