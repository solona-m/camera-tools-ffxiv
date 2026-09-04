using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCameraBase = FFXIVClientStructs.FFXIV.Client.Game.CameraBase;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace CameraToolsXIV.Camera;

/// <summary>A read of the camera as the game last rendered it.</summary>
/// <param name="Position">
/// The eye position recovered from the view matrix -- the camera's true world position.
/// </param>
/// <param name="ScenePosition">
/// The scene camera's own Position field, kept alongside for diagnosis. It is not assumed
/// to equal <paramref name="Position"/>; if the two disagree, the engine is storing
/// something other than the eye point there.
/// </param>
internal readonly record struct CameraSnapshot(
    bool Valid,
    Vector3 Position,
    ViewBasis Basis,
    float FovRadians,
    Vector3 ScenePosition,
    float AspectRatio)
{
    public static CameraSnapshot Invalid => new(false, Vector3.Zero, ViewBasis.Identity, 0f, Vector3.Zero, 1f);

    /// <summary>
    /// Horizontal field of view in radians, derived from the vertical one.
    /// </summary>
    /// <remarks>
    /// FFXIV stores a vertical FoV. Marty's Parallax DoF declares its uniform as
    /// "hor FOV, rad" and scales its reprojection by <c>tan(FOV * 0.5)</c>, so handing it
    /// the vertical angle understates the scale by the aspect ratio.
    /// </remarks>
    public float HorizontalFovRadians
        => 2f * MathF.Atan(MathF.Tan(this.FovRadians * 0.5f) * (this.AspectRatio > 0f ? this.AspectRatio : 1f));
}

/// <summary>
/// Tracks the game camera and, while an add-on is stacking frames, takes it over.
/// </summary>
/// <remarks>
/// <para>
/// The override is deliberately scoped to the duration of an add-on session rather than
/// to a user toggle. Depth of field only needs us to own the camera for the seconds it
/// spends stepping around an aperture; holding it any longer than that would fight
/// whatever the user framed the shot with (GPose's own controls, Cammy, Brio) for no
/// benefit. Outside a session this class only reads.
/// </para>
/// <para>
/// The override has to happen <i>after</i> <c>CameraBase::Update</c>, not before and not
/// on a framework tick. The game recomputes the camera transform from its target and
/// input every frame, so anything we write beforehand is simply overwritten, and a
/// framework tick has no ordering guarantee relative to that update. Hooking the vtable
/// slot and writing on the way out is the only placement that reliably wins.
/// </para>
/// </remarks>
internal sealed unsafe class CameraController : IDisposable
{
    /// <summary>Index of <c>Update</c> in CameraBaseVirtualTable (offset 0x18 / 8).</summary>
    private const int UpdateVTableIndex = 3;

    private delegate void CameraUpdateDelegate(GameCameraBase* camera);

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly object gate = new();

    /// <summary>Minimum gap between camera-update error logs, in milliseconds.</summary>
    private const long ErrorLogIntervalMs = 60_000;

    private Hook<CameraUpdateDelegate>? updateHook;
    private bool hookAbandoned;
    private bool waitingLogged;
    private long lastErrorLogMs = long.MinValue;

    // The game's code section, used to sanity-check addresses read out of the camera's
    // vtable before handing them to the hooking library.
    private readonly nint moduleStart;
    private readonly nint moduleEnd;

    // Guarded by `gate`. Sessions run on ReShade's render thread while the hook runs on
    // the game thread, so every one of these crosses a thread boundary.
    private bool armed;
    private bool holding;
    private Vector3 sessionOffset;
    private float? fovOverrideRadians;

    // The camera exactly as the game had it when the hold began. Both the position and
    // the look-at point are kept so that a step can translate the pair together.
    private Vector3 holdPosition;
    private Vector3 holdLookAt;
    private Vector3 holdEye;
    private ViewBasis holdBasis = ViewBasis.Identity;

    // The most recent unmodified values, so that BeginHold can capture them.
    private Vector3 rawPosition;
    private Vector3 rawLookAt;

    private CameraSnapshot lastSnapshot = CameraSnapshot.Invalid;

    // Zero, not long.MinValue: Environment.TickCount64 minus long.MinValue overflows to a
    // negative value, which would read as "fresh" and invert the guard. Zero makes an
    // un-stamped snapshot stale, which is the safe direction.
    private long lastUpdateMs;

    // When the update detour last ran, as distinct from when the snapshot was last
    // refreshed -- the tick refreshes it too, so one timestamp cannot answer both.
    private long lastDetourMs;

    /// <summary>
    /// How long the detour may be silent before the framework tick takes over applying a
    /// hold.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than a frame at any playable rate, so at speed this never fires
    /// and the tick keeps its hands off a camera the game is actively updating. It exists
    /// for the case where the game's update stops but rendering does not -- a paused world
    /// being the obvious one. Nothing rebuilds the matrix then, so a tick write is not
    /// overwritten and is the only thing that can still move the camera.
    /// </remarks>
    private const long DetourQuietForMs = 100;

    /// <summary>
    /// How long a snapshot stays usable without the camera update refreshing it.
    /// </summary>
    /// <remarks>
    /// The detour is bound to one camera type's vtable slot. If the active camera changes
    /// to a type that does not share it -- a cutscene or lobby camera -- the detour stops
    /// firing and nothing else notices. Without an expiry the last snapshot would stay
    /// marked valid forever, so we would keep publishing a camera position from minutes
    /// ago and would happily start a session against it.
    /// </remarks>
    private const long SnapshotStaleAfterMs = 1000;

    /// <summary>
    /// Invoked on the game thread immediately after the camera transform is settled for
    /// the frame.
    /// </summary>
    /// <remarks>
    /// Publishing from here rather than from a framework tick matters: a tick has no
    /// ordering guarantee against the camera update, so it can describe the previous
    /// frame's camera. The add-on reprojects against the data we publish, and feeding it
    /// a frame-old position is indistinguishable to it from the camera having moved
    /// somewhere it did not.
    /// </remarks>
    public Action? TransformSettled { get; set; }

    public CameraController(IGameInteropProvider interop, IPluginLog log)
    {
        this.interop = interop;
        this.log = log;

        try
        {
            using var process = Process.GetCurrentProcess();
            var main = process.MainModule!;
            this.moduleStart = main.BaseAddress;
            this.moduleEnd = main.BaseAddress + main.ModuleMemorySize;
        }
        catch (Exception ex)
        {
            // Without the range we cannot validate, so fall back to attempting the hook
            // and letting it fail loudly rather than silently never hooking.
            this.log.Warning(ex, "Could not determine the game module range; hook address validation is disabled.");
        }
    }

    /// <summary>Whether an address plausibly points at game code.</summary>
    private bool IsGameCode(nint address)
        => this.moduleEnd == 0 || (address >= this.moduleStart && address < this.moduleEnd);

    /// <summary>
    /// The camera as the game last rendered it, or <see cref="CameraSnapshot.Invalid"/> if
    /// the camera update has stopped refreshing it.
    /// </summary>
    public CameraSnapshot LastSnapshot
    {
        get { lock (this.gate) { return this.SnapshotLocked(); } }
    }

    private CameraSnapshot SnapshotLocked()
        => Environment.TickCount64 - this.lastUpdateMs > SnapshotStaleAfterMs
            ? CameraSnapshot.Invalid
            : this.lastSnapshot;

    /// <summary>
    /// Whether the user has made the camera available to ReShade add-ons.
    /// </summary>
    /// <remarks>
    /// Being armed changes nothing about the camera. It only sets the <c>cameraEnabled</c>
    /// flag the add-on reads to decide whether to offer a depth-of-field session at all.
    /// </remarks>
    public bool Armed
    {
        get { lock (this.gate) { return this.armed; } }
    }

    /// <summary>Whether we are currently overriding the camera transform.</summary>
    public bool Holding
    {
        get { lock (this.gate) { return this.holding; } }
    }

    /// <summary>
    /// Whether the game is still calling the camera update this detour sits on.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than inferring. A camera that has stopped moving looks the
    /// same whether the game stopped updating it or the plugin stopped writing to it, and
    /// those want opposite fixes.
    /// </remarks>
    public bool UpdateFiring
    {
        get { lock (this.gate) { return Environment.TickCount64 - this.lastDetourMs <= DetourQuietForMs; } }
    }

    /// <summary>
    /// Whether the update hook is installed. Surfaced because without it the camera can be
    /// read but not driven, and that difference is invisible until a stack fails.
    /// </summary>
    public bool HookInstalled => this.updateHook is not null;

    /// <summary>Whether hooking failed outright and will not be retried.</summary>
    public bool HookAbandoned => this.hookAbandoned;

    /// <summary>
    /// Sets whether add-ons may drive the camera. Driven automatically from the game
    /// state rather than by the user.
    /// </summary>
    /// <remarks>
    /// Arming has no effect on the camera by itself, so there is nothing to be gained by
    /// making it a manual step: an add-on's depth-of-field UI stays hidden until it sees
    /// this flag, and the camera is still only taken over for the duration of a stack.
    /// <para>
    /// Disarming deliberately does <b>not</b> release an active hold. Only the session
    /// that took the camera may give it back: releasing here would leave the session
    /// believing it still owns a camera that had silently stopped moving, which is
    /// invisible to the add-on and produces a ghosted stack. Callers cannot avoid this by
    /// checking for an active session first, because a session can start on the render
    /// thread between the check and the call.
    /// </para>
    /// </remarks>
    public void SetArmed(bool value)
    {
        lock (this.gate)
        {
            this.armed = value;
        }
    }

    /// <summary>
    /// Takes over the camera, freezing it where it currently sits.
    /// </summary>
    /// <returns>False if there is no valid camera to hold.</returns>
    /// <remarks>
    /// Captures the camera's own position and look-at point rather than rebuilding them
    /// from a direction vector. A step then translates the pair by the same offset, which
    /// is a parallel shift by construction -- no assumption about which way the view
    /// matrix's forward axis points, and no invented look-at distance. Both of those are
    /// easy to get backwards, and getting them backwards aims the camera at whatever is
    /// behind it.
    /// <para>
    /// Translating both is also what an accumulation stack requires. Moving the position
    /// while leaving the look-at pinned to a fixed world point would toe the camera in,
    /// and the shader's focus-delta realignment assumes it did not.
    /// </para>
    /// </remarks>
    public bool BeginHold()
    {
        float divergence;

        lock (this.gate)
        {
            var snapshot = this.SnapshotLocked();
            if (!snapshot.Valid)
            {
                return false;
            }

            this.holdPosition = this.rawPosition;
            this.holdLookAt = this.rawLookAt;
            this.holdEye = snapshot.Position;
            this.holdBasis = snapshot.Basis;

            // We write scene->Position but publish the view matrix's eye point, which is
            // only sound while the two translate together. They are identical in FFXIV,
            // and a divergence would silently misreport where the camera is -- presenting
            // as depth of field that cannot be focused -- so measure it here and report it
            // once the locks are released.
            divergence = Vector3.Distance(this.holdEye, this.holdPosition);

            this.sessionOffset = Vector3.Zero;
            this.fovOverrideRadians = null;
            this.holding = true;
        }

        // Outside both locks. This runs on ReShade's render thread with the session lock
        // also held, and the log sink writes to disk -- doing that under the camera lock
        // would stall the game thread's camera update behind a file write.
        if (divergence > 0.01f)
        {
            this.log.Warning(
                $"Camera eye and scene position differ by {divergence:F3} units; " +
                "published position may not match the rendered camera.");
        }

        return true;
    }

    public void ReleaseHold()
    {
        lock (this.gate)
        {
            this.ReleaseHoldLocked();
        }
    }

    private void ReleaseHoldLocked()
    {
        this.holding = false;
        this.sessionOffset = Vector3.Zero;
        this.fovOverrideRadians = null;
    }

    /// <summary>The orientation frozen at the start of the hold.</summary>
    public ViewBasis HoldBasis
    {
        get { lock (this.gate) { return this.holdBasis; } }
    }

    /// <summary>
    /// Rotates the held view about the world up axis, for panorama sessions.
    /// </summary>
    /// <remarks>
    /// Rotates the look-at point around the camera rather than re-deriving it from the
    /// basis, so it stays consistent with how a hold is applied. Rotating about world up
    /// rather than the camera's own up keeps the horizon level on a pitched shot.
    /// </remarks>
    public void RotateHold(float angleRadians)
    {
        lock (this.gate)
        {
            if (!this.holding)
            {
                return;
            }

            var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, angleRadians);

            this.holdLookAt = this.holdPosition + Vector3.Transform(this.holdLookAt - this.holdPosition, rotation);
            this.holdBasis = new ViewBasis(
                Vector3.Normalize(Vector3.Transform(this.holdBasis.Right, rotation)),
                Vector3.Normalize(Vector3.Transform(this.holdBasis.Up, rotation)),
                Vector3.Normalize(Vector3.Transform(this.holdBasis.Forward, rotation)));
        }
    }

    /// <summary>
    /// The offset an add-on applies while stacking frames, relative to the hold origin.
    /// </summary>
    /// <remarks>
    /// Kept separate from the origin so that ending a session restores the user's framing
    /// exactly, with no drift accumulated from summing and un-summing steps.
    /// </remarks>
    public Vector3 SessionOffset
    {
        get { lock (this.gate) { return this.sessionOffset; } }
        set { lock (this.gate) { this.sessionOffset = value; } }
    }

    /// <summary>FoV in radians, or null to leave the game's own value alone.</summary>
    public float? FovOverrideRadians
    {
        get { lock (this.gate) { return this.fovOverrideRadians; } }
        set { lock (this.gate) { this.fovOverrideRadians = value; } }
    }

    /// <summary>
    /// Installs the update hook once a camera exists. Called every frame until it takes;
    /// there is no camera to read a vtable from until the player is in the world.
    /// </summary>
    public void EnsureHooked()
    {
        if (this.updateHook is not null || this.hookAbandoned)
        {
            return;
        }

        var camera = GetActiveCamera();
        if (camera is null)
        {
            return;
        }

        // A camera object exists well before it is usable -- at the title screen and
        // during zone loads its vtable pointer is not yet a real one. Hooking whatever
        // happens to be there hands the hooking library an address nowhere near the
        // game's code, and it fails trying to place a trampoline within reach of it.
        // Validate first and simply try again next frame instead.
        var vtable = *(nint**)camera;
        if (!this.IsGameCode((nint)vtable))
        {
            this.NoteWaiting();
            return;
        }

        var updateAddress = vtable[UpdateVTableIndex];
        if (!this.IsGameCode(updateAddress))
        {
            this.NoteWaiting();
            return;
        }

        try
        {
            this.updateHook = this.interop.HookFromAddress<CameraUpdateDelegate>(updateAddress, this.UpdateDetour);
            this.updateHook.Enable();
            this.log.Information($"Hooked CameraBase::Update at 0x{updateAddress:X}");
        }
        catch (Exception ex)
        {
            // A validated address that still will not hook is a real failure, not a
            // timing problem, so stop retrying and say so.
            this.hookAbandoned = true;
            this.log.Error(ex, "Failed to hook CameraBase::Update; add-on sessions will not be able to drive the camera.");
        }
    }

    private void NoteWaiting()
    {
        if (this.waitingLogged)
        {
            return;
        }

        this.waitingLogged = true;
        this.log.Debug("Camera is not ready yet; waiting for a valid camera to hook.");
    }

    private void UpdateDetour(GameCameraBase* camera)
    {
        this.updateHook!.Original(camera);

        lock (this.gate)
        {
            this.lastDetourMs = Environment.TickCount64;
        }

        try
        {
            this.RefreshAndApply(camera);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the game's update path. Throttled because
            // this runs every frame: an unthrottled fault would log at frame rate and
            // degrade the game through sheer log I/O.
            var now = Environment.TickCount64;
            if (now - this.lastErrorLogMs > ErrorLogIntervalMs)
            {
                this.lastErrorLogMs = now;
                this.log.Error(ex, "Camera update failed.");
            }
        }
    }

    /// <summary>
    /// Reads the active camera without touching it. Safe to call from the framework tick.
    /// </summary>
    /// <remarks>
    /// Reading never needed the hook — only writing does. Tying both to the update detour
    /// meant that whenever it stopped firing, the snapshot expired, the camera reported
    /// itself unavailable, and the add-on was told there was no camera tool. The plugin
    /// then sat with both status lines green and a "waiting for the game camera" that
    /// never cleared, which is indistinguishable from it being broken.
    /// </remarks>
    public void Refresh()
    {
        var camera = GetActiveCamera();
        if (camera is null)
        {
            return;
        }

        // Normally read-only: the game's own update runs later in the frame and would
        // overwrite anything written here, so applying a hold from the tick would be at
        // best pointless and at worst a frame of flicker. When the detour has gone quiet
        // there is no such update to lose to, and the tick becomes the only writer left.
        bool applyHold;
        lock (this.gate)
        {
            applyHold = Environment.TickCount64 - this.lastDetourMs > DetourQuietForMs;
        }

        try
        {
            this.RefreshAndApply(camera, applyHold);
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (now - this.lastErrorLogMs > ErrorLogIntervalMs)
            {
                this.lastErrorLogMs = now;
                this.log.Error(ex, "Camera read failed.");
            }
        }
    }

    private void RefreshAndApply(GameCameraBase* camera, bool applyHold = true)
    {
        if (camera is null || camera != GetActiveCamera())
        {
            return;
        }

        var scene = &camera->SceneCamera;
        var render = scene->RenderCamera;
        if (render is null)
        {
            return;
        }

        // What the game just produced. Outside a hold this is the whole story, and it is
        // what seeds the hold when a session starts.
        //
        // Explicitly typed: FFXIVClientStructs has its own Vector3 that converts
        // implicitly to this one, and `var` would leave arithmetic below ambiguous
        // between the two.
        Vector3 scenePosition = scene->Position;
        Vector3 sceneLookAt = scene->LookAtVector;
        var basis = ViewBasis.FromViewMatrix(render->ViewMatrix);
        var fov = render->FoV;
        var aspect = render->AspectRatio;

        // The eye position recovered from the view matrix, which is the camera's true
        // world position by definition. The scene camera's own Position field is not
        // assumed to be the same thing.
        var eye = basis.PositionFromViewMatrix(render->ViewMatrix);

        lock (this.gate)
        {
            this.rawPosition = scenePosition;
            this.rawLookAt = sceneLookAt;

            // The write only happens from the update hook. A framework-tick read would be
            // overwritten by the game's own update later in the frame, so applying a hold
            // there would flicker rather than hold.
            if (this.holding)
            {
                if (applyHold)
                {
                    // Translate the position and the look-at point by the same offset. This
                    // preserves the framing the user set, and is a parallel shift whatever
                    // the engine's axis conventions turn out to be.
                    scene->Position = this.holdPosition + this.sessionOffset;
                    scene->LookAtVector = this.holdLookAt + this.sessionOffset;

                    // Then write the matrices those two feed, rather than leaving the game
                    // to rebuild them.
                    //
                    // Position and LookAtVector are inputs. We write them on the way out of
                    // an update that has already built this frame's matrices from the old
                    // values, so they only take effect when the game runs the update again.
                    // That is fine at speed and useless when the world is paused: the
                    // rebuild never comes, the renderer keeps drawing the matrix it has, and
                    // the camera sits still through an entire stack while we cheerfully
                    // publish the position it was supposed to have moved to.
                    //
                    // Writing the matrix removes the dependency and the frame of lag with
                    // it. Both copies go together: the render camera is what the renderer
                    // consumes, and the scene camera's is what ScreenPointToRay and
                    // WorldToScreen read, so letting them disagree would put every
                    // world-to-screen answer in the game a frame behind the picture.
                    var view = this.holdBasis.ToViewMatrix(this.holdEye + this.sessionOffset);
                    render->ViewMatrix = view;
                    scene->ViewMatrix = view;

                    if (this.fovOverrideRadians is { } writeFov)
                    {
                        render->FoV = writeFov;
                    }
                }

                // Reported regardless of who is reading. A tick read during a hold must not
                // publish the game's own values: the add-on reprojects against what we
                // report, and telling it the camera is somewhere other than where we are
                // holding it is exactly the inconsistency the hold exists to avoid.
                if (this.fovOverrideRadians is { } reportFov)
                {
                    fov = reportFov;
                }

                eye = this.holdEye + this.sessionOffset;
                basis = this.holdBasis;
            }

            this.lastSnapshot = new CameraSnapshot(true, eye, basis, fov, scenePosition, aspect);
            this.lastUpdateMs = Environment.TickCount64;
        }

        // Outside the lock: the callback publishes to the add-on and must not be able to
        // hold up the camera update, or be re-entered while the lock is held.
        this.TransformSettled?.Invoke();
    }

    private static GameCameraBase* GetActiveCamera()
    {
        var manager = CameraManager.Instance();
        if (manager is null)
        {
            return null;
        }

        return (GameCameraBase*)manager->GetActiveCamera();
    }

    public void Dispose()
    {
        this.updateHook?.Disable();
        this.updateHook?.Dispose();
        this.updateHook = null;
    }
}
