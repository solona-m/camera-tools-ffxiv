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
    Vector3 ScenePosition)
{
    public static CameraSnapshot Invalid => new(false, Vector3.Zero, ViewBasis.Identity, 0f, Vector3.Zero);
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

    private Hook<CameraUpdateDelegate>? updateHook;
    private bool hookAbandoned;
    private bool waitingLogged;

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

    /// <summary>The camera as the game last rendered it, refreshed every update.</summary>
    public CameraSnapshot LastSnapshot
    {
        get { lock (this.gate) { return this.lastSnapshot; } }
    }

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
    /// Sets whether add-ons may drive the camera. Driven automatically from the game
    /// state rather than by the user.
    /// </summary>
    /// <remarks>
    /// Arming has no effect on the camera by itself, so there is nothing to be gained by
    /// making it a manual step: an add-on's depth-of-field UI stays hidden until it sees
    /// this flag, and the camera is still only taken over for the duration of a stack.
    /// </remarks>
    public void SetArmed(bool value)
    {
        lock (this.gate)
        {
            if (this.armed == value)
            {
                return;
            }

            this.armed = value;

            if (!value)
            {
                this.ReleaseHoldLocked();
            }
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
        lock (this.gate)
        {
            if (!this.lastSnapshot.Valid)
            {
                return false;
            }

            this.holdPosition = this.rawPosition;
            this.holdLookAt = this.rawLookAt;
            this.holdEye = this.lastSnapshot.Position;
            this.holdBasis = this.lastSnapshot.Basis;
            this.sessionOffset = Vector3.Zero;
            this.fovOverrideRadians = null;
            this.holding = true;
            return true;
        }
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

        try
        {
            this.RefreshAndApply(camera);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the game's update path.
            this.log.Error(ex, "Camera update failed.");
        }
    }

    private void RefreshAndApply(GameCameraBase* camera)
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

        // The eye position recovered from the view matrix, which is the camera's true
        // world position by definition. The scene camera's own Position field is not
        // assumed to be the same thing.
        var eye = basis.PositionFromViewMatrix(render->ViewMatrix);

        lock (this.gate)
        {
            this.rawPosition = scenePosition;
            this.rawLookAt = sceneLookAt;

            if (this.holding)
            {
                // Translate the position and the look-at point by the same offset. This
                // preserves the framing the user set, and is a parallel shift whatever
                // the engine's axis conventions turn out to be.
                scene->Position = this.holdPosition + this.sessionOffset;
                scene->LookAtVector = this.holdLookAt + this.sessionOffset;

                if (this.fovOverrideRadians is { } overrideFov)
                {
                    render->FoV = overrideFov;
                    fov = overrideFov;
                }

                eye = this.holdEye + this.sessionOffset;
                basis = this.holdBasis;
            }

            this.lastSnapshot = new CameraSnapshot(true, eye, basis, fov, scenePosition);
        }
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
