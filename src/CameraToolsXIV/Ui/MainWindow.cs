using System;
using System.Numerics;
using CameraToolsXIV.Camera;
using CameraToolsXIV.Igcs;
using CameraToolsXIV.World;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace CameraToolsXIV.Ui;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.90f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Configuration configuration;
    private readonly CameraController camera;
    private readonly ScreenshotSession session;
    private readonly IgcsBridge bridge;
    private readonly ConnectorLink connector;
    private readonly PhysicsFreeze physicsFreeze;
    private readonly WorldPause worldPause;
    private readonly Func<bool> isCameraAllowed;
    private readonly Action saveConfiguration;

    public MainWindow(
        Configuration configuration,
        CameraController camera,
        ScreenshotSession session,
        IgcsBridge bridge,
        ConnectorLink connector,
        PhysicsFreeze physicsFreeze,
        WorldPause worldPause,
        Func<bool> isCameraAllowed,
        Action saveConfiguration)
        : base("Camera Tools###CameraToolsXIVMain")
    {
        this.configuration = configuration;
        this.camera = camera;
        this.session = session;
        this.bridge = bridge;
        this.connector = connector;
        this.physicsFreeze = physicsFreeze;
        this.worldPause = worldPause;
        this.isCameraAllowed = isCameraAllowed;
        this.saveConfiguration = saveConfiguration;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        this.DrawStatus();
        ImGui.Separator();
        this.DrawCameraControls();
        ImGui.Separator();
        this.DrawLiveValues();
        ImGui.Separator();
        this.DrawSettings();
    }

    private void DrawStatus()
    {
        ImGui.TextUnformatted("ReShade link");
        ImGui.Indent();

        if (this.bridge.Loaded)
        {
            ImGui.TextColored(Good, "IGCS exports active");
        }
        else
        {
            ImGui.TextColored(Bad, "IGCS exports unavailable");
            if (this.bridge.LoadError is { } error)
            {
                ImGui.TextWrapped(error);
            }
        }

        if (this.connector.Connected)
        {
            ImGui.TextColored(Good, $"Connected to {this.connector.ConnectedModule}");
        }
        else
        {
            ImGui.TextColored(Muted, "No ReShade add-on connected");
            ImGui.TextWrapped(
                "Install ReShade 6.4+ with add-on support plus iMMERSE Parallax DoF or " +
                "Otis_Inf's IgcsConnector, then reload this plugin.");
        }

        // The camera hook is separate from the ReShade link and fails separately. Without
        // it the camera can be read but never driven, which otherwise only shows up as a
        // stack that quietly does nothing.
        if (this.camera.HookAbandoned)
        {
            ImGui.TextColored(Bad, "Camera hook failed -- add-ons cannot drive the camera");
        }
        else if (!this.camera.HookInstalled)
        {
            ImGui.TextColored(Muted, "Waiting to hook the camera...");
        }
        else
        {
            ImGui.TextColored(Good, "Camera hook installed");
        }

        if (this.session.Active)
        {
            ImGui.TextColored(Good, "Add-on session in progress");
        }

        ImGui.Unindent();
    }

    private void DrawCameraControls()
    {
        if (!this.camera.LastSnapshot.Valid)
        {
            ImGui.TextColored(Muted, "Waiting for the game camera...");
            return;
        }

        if (this.session.Active)
        {
            ImGui.TextColored(Good, "Camera held by the add-on for this stack.");

            // Shown live during a stack: this is what to watch when calibrating the step
            // scale, since the add-on's raw values mean nothing until they are converted.
            var type = this.session.SessionType;
            ImGui.TextUnformatted(
                $"type   {type,9}  ({(type == 0 ? "panorama" : type == 1 ? "multishot" : "unknown")})");

            var raw = this.session.LastRawStep;
            var offset = this.session.LastOffset;
            ImGui.TextUnformatted($"step   {raw.X,9:F3} {raw.Y,9:F3}  (add-on units)");
            ImGui.TextUnformatted($"moved  {offset.Length(),9:F3}  world units");

            // Both move calls are shown, always. A camera that is visibly sweeping while
            // every number reads zero means the add-on is driving the other call, and that
            // is the single most useful thing this panel can tell you.
            var (total, calls) = this.session.PanoramaTotal;
            ImGui.TextUnformatted(
                $"rotate {this.session.LastPanoramaAngle,9:F3} rad  " +
                $"({float.RadiansToDegrees(this.session.LastPanoramaAngle):F1} deg)");
            ImGui.TextUnformatted(
                $"swept  {float.RadiansToDegrees(total),9:F1} deg over {calls} calls");

            // Worth showing rather than assuming: both are derived a frame behind the
            // session, and both are the kind of thing that is invisible until you compare
            // two stacks and wonder why one ghosted.
            if (this.physicsFreeze.Frozen)
            {
                ImGui.TextColored(Good, "Physics frozen");
            }

            if (this.worldPause.Paused)
            {
                ImGui.TextColored(Good, "World paused");
            }

            // The two ways a stack can silently go wrong look identical on screen, so name
            // which one is happening: the game no longer updating the camera, or the plugin
            // no longer writing to it.
            ImGui.TextColored(
                this.camera.UpdateFiring ? Muted : Bad,
                this.camera.UpdateFiring
                    ? "camera update firing"
                    : "camera update stopped -- holding from the framework tick");

            // The only way out if an add-on dies mid-stack without ending its session.
            if (ImGui.SmallButton("Release camera"))
            {
                this.session.Abort();
            }

            return;
        }

        if (this.session.WasAborted)
        {
            ImGui.TextColored(Bad, "Last session was cut short.");
            ImGui.TextWrapped(
                "The add-on was not told and may still think it is stacking. Press CANCEL " +
                "in its window and start again.");
        }

        // Availability is derived from the game state, not chosen by the user: arming has
        // no effect on its own, so a manual toggle would be friction for nothing.
        if (this.camera.Armed)
        {
            ImGui.TextColored(Good, "Camera available -- depth of field can run.");
            ImGui.TextWrapped(
                "Frame the shot however you like -- Group Pose, Cammy, Brio. The camera is " +
                "only taken over while an add-on is stacking frames, and handed straight " +
                "back afterwards.");
        }
        else
        {
            ImGui.TextColored(Muted, "Camera not available.");
            ImGui.TextWrapped(
                "Enter Group Pose to make the camera available to ReShade add-ons, or turn " +
                "on \"Allow outside Group Pose\" below.");
        }
    }

    private void DrawLiveValues()
    {
        var snapshot = this.camera.LastSnapshot;
        if (!snapshot.Valid)
        {
            return;
        }

        var (pitch, yaw, roll) = snapshot.Basis.ToEuler();

        // These are the exact values published to the add-on. When calibrating against
        // IgcsSourceTester.fx, this panel and the shader overlay should agree.
        ImGui.TextUnformatted("Published to ReShade");
        ImGui.Indent();
        ImGui.TextUnformatted($"pos    {snapshot.Position.X,9:F3} {snapshot.Position.Y,9:F3} {snapshot.Position.Z,9:F3}");
        ImGui.TextUnformatted($"scene  {snapshot.ScenePosition.X,9:F3} {snapshot.ScenePosition.Y,9:F3} {snapshot.ScenePosition.Z,9:F3}");
        var publishedFov = this.configuration.PublishHorizontalFov
            ? snapshot.HorizontalFovRadians
            : snapshot.FovRadians;
        ImGui.TextUnformatted(
            $"fov    {float.RadiansToDegrees(publishedFov),9:F3} deg " +
            $"({(this.configuration.PublishHorizontalFov ? "horizontal" : "vertical")})");
        ImGui.TextUnformatted($"vfov   {float.RadiansToDegrees(snapshot.FovRadians),9:F3} deg  aspect {snapshot.AspectRatio:F3}");
        ImGui.TextUnformatted($"right  {snapshot.Basis.Right.X,9:F3} {snapshot.Basis.Right.Y,9:F3} {snapshot.Basis.Right.Z,9:F3}");
        ImGui.TextUnformatted($"up     {snapshot.Basis.Up.X,9:F3} {snapshot.Basis.Up.Y,9:F3} {snapshot.Basis.Up.Z,9:F3}");
        ImGui.TextUnformatted($"fwd    {snapshot.Basis.Forward.X,9:F3} {snapshot.Basis.Forward.Y,9:F3} {snapshot.Basis.Forward.Z,9:F3}");
        ImGui.TextUnformatted($"p/y/r  {pitch,9:F3} {yaw,9:F3} {roll,9:F3} rad");
        ImGui.Unindent();
    }

    private void DrawSettings()
    {
        // Open by default: there is little enough here that collapsing it would hide the
        // whole panel to save one line.
        if (!ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var horizontalFov = this.configuration.PublishHorizontalFov;
        if (ImGui.Checkbox("Publish horizontal FoV", ref horizontalFov))
        {
            this.configuration.PublishHorizontalFov = horizontalFov;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Parallax DoF declares its FoV uniform as horizontal, but the game\n" +
                "stores vertical. Leave this on for Marty's shaders; turn it off if\n" +
                "another add-on expects vertical.");
        }

        ImGui.Separator();

        this.DrawFreezeSettings();

        ImGui.Separator();

        var allowOutside = this.configuration.AllowOutsideGpose;
        if (ImGui.Checkbox("Allow outside Group Pose", ref allowOutside))
        {
            this.configuration.AllowOutsideGpose = allowOutside;
            this.saveConfiguration();
        }

        if (allowOutside)
        {
            ImGui.TextColored(
                Bad,
                "An untethered camera during normal play sees through walls and terrain.");
        }
    }

    /// <summary>
    /// The two "hold the scene still" settings, which are about the subject rather than the
    /// camera and so are kept apart from the calibration controls above.
    /// </summary>
    private void DrawFreezeSettings()
    {
        var freezePhysics = this.configuration.FreezePhysicsDuringSession;

        // Greyed rather than hidden. A missing signature is worth seeing: it is the one
        // thing here that a game patch can take away, and silently dropping the freeze
        // would show up later as ghosting nobody could account for.
        using (ImRaii.Disabled(!this.physicsFreeze.Available))
        {
            if (ImGui.Checkbox("Freeze physics during a stack", ref freezePhysics))
            {
                this.configuration.FreezePhysicsDuringSession = freezePhysics;
                this.saveConfiguration();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Stops hair, cloth and tails moving for the seconds a stack takes.\n" +
                "Group Pose does not do this on its own, and a swinging skirt ghosts\n" +
                "harder across a stack than anything else in shot.\n\n" +
                "Applies to every character in frame, not just yours.");
        }

        if (this.physicsFreeze.UnavailableReason is { } reason)
        {
            ImGui.TextColored(Bad, reason);
            ImGui.TextWrapped(
                "Everything else still works. Freeze physics in Brio instead until this " +
                "plugin is updated for the new game version.");
        }

        var pauseWorld = this.configuration.PauseWorldDuringSession;
        if (ImGui.Checkbox("Pause the world during a stack", ref pauseWorld))
        {
            this.configuration.PauseWorldDuringSession = pauseWorld;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Stops the game's clock, so water, foliage, weather and particles hold\n" +
                "still along with the actors.\n\n" +
                "There is no narrower way to reach those, so this stops everything else\n" +
                "the game ticks as well -- chat and movement included. It releases when\n" +
                "the stack ends, and after twenty minutes regardless.");
        }

        if (pauseWorld)
        {
            ImGui.TextColored(
                Muted,
                "The game stops responding for the length of a stack.");
        }
    }
}
