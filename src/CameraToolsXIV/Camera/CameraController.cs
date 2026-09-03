using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCameraBase = FFXIVClientStructs.FFXIV.Client.Game.CameraBase;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace CameraToolsXIV.Camera;

/// <summary>A read of the camera as the game last rendered it.</summary>
internal readonly record struct CameraSnapshot(
    bool Valid,
    Vector3 Position,
    ViewBasis Basis,
    float FovRadians)
{
    public static CameraSnapshot Invalid => new(false, Vector3.Zero, ViewBasis.Identity, 0f);
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

    /// <summary>Distance used to project the look-at point along the view direction.</summary>
    private const float LookAtDistance = 10f;

    private delegate void CameraUpdateDelegate(GameCameraBase* camera);

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private Hook<CameraUpdateDelegate>? updateHook;
    private bool hookFailed;

    // Guarded by `gate`. Sessions run on ReShade's render thread while the hook runs on
    // the game thread, so every one of these crosses a thread boundary.
    private bool armed;
    private bool holding;
    private Vector3 holdOrigin;
    private ViewBasis holdBasis = ViewBasis.Identity;
    private Vector3 sessionOffset;
    private float? fovOverrideRadians;

    private CameraSnapshot lastSnapshot = CameraSnapshot.Invalid;

    public CameraController(IGameInteropProvider interop, IPluginLog log)
    {
        this.interop = interop;
        this.log = log;
    }

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

    public bool Arm()
    {
        if (!this.LastSnapshot.Valid)
        {
            return false;
        }

        lock (this.gate)
        {
            this.armed = true;
        }

        return true;
    }

    public void Disarm()
    {
        lock (this.gate)
        {
            this.armed = false;
            this.ReleaseHoldLocked();
        }
    }

    /// <summary>
    /// Takes over the camera, freezing it where it currently sits.
    /// </summary>
    /// <returns>False if there is no valid camera to hold.</returns>
    /// <remarks>
    /// The basis is frozen along with the position on purpose. An accumulation stack has
    /// to be a set of parallel translations: if the camera kept looking at a fixed world
    /// point while stepping sideways it would toe in, and the shader's focus-delta
    /// realignment assumes it did not.
    /// </remarks>
    public bool BeginHold()
    {
        lock (this.gate)
        {
            if (!this.lastSnapshot.Valid)
            {
                return false;
            }

            this.holdOrigin = this.lastSnapshot.Position;
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
        set { lock (this.gate) { this.holdBasis = value; } }
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
        if (this.updateHook is not null || this.hookFailed)
        {
            return;
        }

        var camera = GetActiveCamera();
        if (camera is null)
        {
            return;
        }

        try
        {
            var vtable = *(nint**)camera;
            var updateAddress = vtable[UpdateVTableIndex];

            this.updateHook = this.interop.HookFromAddress<CameraUpdateDelegate>(updateAddress, this.UpdateDetour);
            this.updateHook.Enable();
            this.log.Information($"Hooked CameraBase::Update at 0x{updateAddress:X}");
        }
        catch (Exception ex)
        {
            this.hookFailed = true;
            this.log.Error(ex, "Failed to hook CameraBase::Update; add-on sessions will not be able to drive the camera.");
        }
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
        Vector3 position = scene->Position;
        var basis = ViewBasis.FromViewMatrix(render->ViewMatrix);
        var fov = render->FoV;

        lock (this.gate)
        {
            if (this.holding)
            {
                position = this.holdOrigin + this.sessionOffset;
                basis = this.holdBasis;
                fov = this.fovOverrideRadians ?? fov;

                scene->Position = position;
                scene->LookAtVector = position + (basis.Forward * LookAtDistance);
                render->FoV = fov;
            }

            this.lastSnapshot = new CameraSnapshot(true, position, basis, fov);
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
