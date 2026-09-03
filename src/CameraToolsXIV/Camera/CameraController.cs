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
/// Owns the game camera: reads its true transform, and while the free camera is enabled,
/// overwrites it after the game's own update has run.
/// </summary>
/// <remarks>
/// The override has to happen <i>after</i> <c>CameraBase::Update</c>, not before and not
/// on a framework tick. The game recomputes the camera transform from its target and
/// input every frame, so anything we write beforehand is simply overwritten, and a
/// framework tick has no ordering guarantee relative to that update. Hooking the vtable
/// slot and writing on the way out is the only placement that reliably wins.
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

    // Guarded by `gate`: written from the ReShade render thread, read on the game thread.
    private bool enabled;
    private Vector3 basePosition;
    private Vector3 sessionOffset;
    private ViewBasis basis = ViewBasis.Identity;
    private float fovRadians;
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

    public bool Enabled
    {
        get { lock (this.gate) { return this.enabled; } }
    }

    /// <summary>World-space basis of the camera, used to resolve add-on step offsets.</summary>
    public ViewBasis Basis
    {
        get { lock (this.gate) { return this.basis; } }
    }

    /// <summary>Points the free camera in a new direction.</summary>
    public void SetBasis(ViewBasis value)
    {
        lock (this.gate)
        {
            this.basis = value;
        }
    }

    /// <summary>
    /// Takes over the camera, seeding our state from wherever the game camera currently
    /// is so that enabling the free camera never visibly jumps the view.
    /// </summary>
    public bool Enable()
    {
        var snapshot = this.lastSnapshot;
        if (!snapshot.Valid)
        {
            return false;
        }

        lock (this.gate)
        {
            this.basePosition = snapshot.Position;
            this.basis = snapshot.Basis;
            this.fovRadians = snapshot.FovRadians;
            this.sessionOffset = Vector3.Zero;
            this.fovOverrideRadians = null;
            this.enabled = true;
        }

        return true;
    }

    public void Disable()
    {
        lock (this.gate)
        {
            this.enabled = false;
            this.sessionOffset = Vector3.Zero;
            this.fovOverrideRadians = null;
        }
    }

    /// <summary>Moves the free camera itself, as opposed to an add-on's temporary offset.</summary>
    public void Translate(Vector3 worldDelta)
    {
        lock (this.gate)
        {
            this.basePosition += worldDelta;
        }
    }

    public Vector3 BasePosition
    {
        get { lock (this.gate) { return this.basePosition; } }
        set { lock (this.gate) { this.basePosition = value; } }
    }

    /// <summary>
    /// The transient offset an add-on applies while stacking frames. Kept separate from
    /// <see cref="BasePosition"/> so that ending a session restores the user's framing
    /// exactly, with no accumulated drift from summing and un-summing steps.
    /// </summary>
    public Vector3 SessionOffset
    {
        get { lock (this.gate) { return this.sessionOffset; } }
        set { lock (this.gate) { this.sessionOffset = value; } }
    }

    /// <summary>FoV in radians, or null to keep whatever the free camera was seeded with.</summary>
    public float? FovOverrideRadians
    {
        get { lock (this.gate) { return this.fovOverrideRadians; } }
        set { lock (this.gate) { this.fovOverrideRadians = value; } }
    }

    public float FovRadians
    {
        get { lock (this.gate) { return this.fovOverrideRadians ?? this.fovRadians; } }
        set { lock (this.gate) { this.fovRadians = value; } }
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
            this.log.Error(ex, "Failed to hook CameraBase::Update; the free camera will not engage.");
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

        // Read the transform the game just produced. Even while we are overriding, this
        // is what feeds the data we publish to ReShade, so it stays ground truth.
        var currentBasis = ViewBasis.FromViewMatrix(render->ViewMatrix);
        var currentPosition = scene->Position;
        var currentFov = render->FoV;

        bool active;
        Vector3 target;
        ViewBasis targetBasis;
        float targetFov;

        lock (this.gate)
        {
            active = this.enabled;
            if (!active)
            {
                // Track the game camera so that enabling later starts from here.
                this.basis = currentBasis;
                this.fovRadians = currentFov;
                this.basePosition = currentPosition;
            }

            target = this.basePosition + this.sessionOffset;
            targetBasis = this.basis;
            targetFov = this.fovOverrideRadians ?? this.fovRadians;
        }

        if (active)
        {
            scene->Position = target;
            scene->LookAtVector = target + (targetBasis.Forward * LookAtDistance);
            render->FoV = targetFov;

            currentPosition = target;
            currentFov = targetFov;
        }

        lock (this.gate)
        {
            this.lastSnapshot = new CameraSnapshot(true, currentPosition, active ? targetBasis : currentBasis, currentFov);
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
