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

### Holding the scene still

An accumulation stack composites many frames of the same moment, so anything that moves
between them ghosts. Group Pose stops actors *acting* but not their physics, and a skirt
or a length of hair swinging through a stack ghosts harder than anything else in shot,
because it is high-contrast and sits right on the subject.

So for the seconds a stack takes — and only those seconds — the plugin hooks
`hkaPartialSkeleton::SetBoneModelTransform` and drops the simulation's result. That is the
shared havok write path, so the freeze covers every drawn skeleton at once: you, other
players, NPCs, minions, mounts. This is the same thing Brio's "Freeze Physics" button does,
done here so there is one less plugin to run and one less thing to remember to press. It
needs a signature, so a game patch can take it away; when that happens the plugin says so
in its window and everything else carries on.

**"Pause the world"** goes after the rest of it, and is also on by default. Freezing
physics leaves the environment moving, and the only lever that reaches water, foliage,
weather and particles is the game's frame delta itself — the same one the game drives to
nothing when it puts up a message box. Stopping it stops chat and movement too, which is a
real cost and the default anyway: a 1024-sample stack occupies the game for four minutes
whichever way you set this, and finding out afterwards that the foliage moved is worse
than knowing in advance that chat will be quiet until it finishes. It is bounded by the
stack, and released after twenty minutes regardless — long enough to clear any plausible
render, since a watchdog that fires mid-stack thaws the world for the back half and ruins
it more thoroughly than never freezing at all.

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

You can rebuild while the game is running. The plugin loads the shim from a copy in the
temp directory rather than in place, because a mapped DLL stays locked and this one is
never unmapped — add-ons cache pointers into it. Loading it in place would mean the game
holds the build's own `IgcsBridge.dll` open and every rebuild fails on a file-copy error.

The build also runs `tests/IgcsBridgeHarness`, which exercises the add-on boundary
without needing the game: it registers callbacks the way the plugin does, then discovers
and calls the exports the way an add-on does. Marshalling mistakes there produce
plausible-looking garbage rather than a crash, so they are worth catching on the desktop.

## Status

Working: the IGCS export surface, add-on discovery and per-frame data publishing, the
session-scoped camera override, the screenshot-session semantics that depth of field
drives, and the session-scoped physics freeze and world pause.

Confirmed in-game: a **completed 1024-sample stack**, sharp to the freckles, with no
ghosting on hair or cloth. Parallax DoF discovers the exports by module scan, connects,
and drives the camera through a session. The published basis checks out as orthonormal and
left-handed consistent (`right × up` equals `fwd` exactly), and the step conversion is
exactly 1:1 — a blur radius of *r* produces an offset of *r* world units, measurable on
the plugin's own panel.

Deliberately not built: fly controls. Parallax DoF needs us to *own* the camera position,
not to move it for you, and Cammy and Brio already do camera movement well. Still open,
if they turn out to be wanted: camera paths with interpolated playback, presets, and
rebindable hotkeys.

### Suggested Parallax DoF settings

A starting point that produces a clean stack in Group Pose. The only value here that is
really *about FFXIV* is the blur radius; the rest is taste.

| Setting | Value |
|---|---|
| Accumulation Init / Delay | 4 frames each |
| Rangefinder Focus | ~15.1 (per shot) |
| **Blur Radius** | **0.003** |
| Bokeh Intensity / Gamma / Colour | 0.550 / 0.580 / 0.370 |
| Aperture | Circular, aspect 1.000 |
| Sample Count | 1024 (about four minutes) |

**Blur Radius is the one to get right, and it is the aperture in FFXIV world units.** The
plugin converts add-on steps 1:1, so a radius of 1.0 — Parallax DoF's default — asks for a
one-metre aperture. FFXIV's unit is roughly a metre, and Marty's default assumes a game
whose units are far smaller. A metre of baseline sees *behind* your subject: you get two
separate exposures rather than a blur, and no focus setting rescues it, because the
parallax is real. **0.003 is about three millimetres**, which is a lens.

Watch the plugin's `moved` line while the setup preview runs — it reads the offset in
world units, and it should equal the blur radius exactly. That is the fastest way to
confirm the whole chain is behaving.

**Rangefinder Focus is logarithmic** (`exp2(FOCUS_DELTA - 16)`), so a fraction of a slider
unit moves the focal plane a long way and it is easy to overshoot the subject onto the
background. If a render comes back with a blurred foreground and a crisp background, the
focal plane has landed behind your subject — walk the rangefinder back rather than
touching anything else. Values below about 10 are indistinguishable from zero.

Reduce the radius further for less background streaking. Parallax DoF only has the pixels
the game rendered and knows nothing about what sits behind your subject, so at depth it
smears rather than forming clean bokeh discs. A hedge two metres back bokehs cleanly; a
treeline fifteen metres back will always streak. That is the technique, not a fault.

Disable V-Sync, per Marty's own guidance — the accumulation frames are stepped, and V-Sync
desynchronises the stepping.

### Calibration

Before trusting a depth-of-field stack, confirm the published data is right. Install
`IgcsSourceTester.fx` from the IgcsConnector repository — it draws every `IGCS_*` uniform
on screen. Those values and the plugin's "Published to ReShade" panel should agree, and
should track as you move the camera.

## Limitations

- **The world can be held still, at a price.** Group Pose freezes what actors *do*, not
  what hangs off them, so the plugin freezes bone physics itself and pauses the game's
  clock for the length of a stack. Both are on by default. The pause is the blunt one: the
  game stops responding — chat, movement, everything — until the stack finishes.
- **Background streaking is inherent to parallax depth of field.** The shader only has the
  pixels the game rendered and cannot know what sits behind your subject, so distant
  detail smears instead of forming bokeh discs. Manageable by shot selection and a smaller
  aperture; not fixable here.
- Disable V-Sync for frame-step synchronisation, per Marty's own guidance.
- The camera is only offered to add-ons in Group Pose by default. The setting to allow it
  during normal play is deliberately opt-in: a camera an add-on can reposition freely in
  the overworld sees through walls and terrain.
- Dalamud plugins are against FFXIV's Terms of Service. This is client-side visual
  tooling in the same category as Brio and Ktisis.

## Credits

The IGCS camera tool interface is the work of Frans 'Otis_Inf' Bouma
([IGCS](https://github.com/FransBouma/InjectableGenericCameraSystem),
[IgcsConnector](https://github.com/FransBouma/IgcsConnector), BSD licensed). Parallax
Depth of Field is by Pascal 'Marty McFly' Gilcher.
