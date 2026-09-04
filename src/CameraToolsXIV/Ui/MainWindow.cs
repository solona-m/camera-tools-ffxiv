using System;
using System.Numerics;
using CameraToolsXIV.Camera;
using CameraToolsXIV.Igcs;
using Dalamud.Bindings.ImGui;
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
    private readonly Func<bool> isCameraAllowed;
    private readonly Action saveConfiguration;

    public MainWindow(
        Configuration configuration,
        CameraController camera,
        ScreenshotSession session,
        IgcsBridge bridge,
        ConnectorLink connector,
        Func<bool> isCameraAllowed,
        Action saveConfiguration)
        : base("Camera Tools###CameraToolsXIVMain")
    {
        this.configuration = configuration;
        this.camera = camera;
        this.session = session;
        this.bridge = bridge;
        this.connector = connector;
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
            var raw = this.session.LastRawStep;
            var offset = this.session.LastOffset;
            ImGui.TextUnformatted($"step   {raw.X,9:F3} {raw.Y,9:F3}  (add-on units)");
            ImGui.TextUnformatted($"moved  {offset.Length(),9:F3}  world units");

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
        // Open by default: the step scale needs calibrating per setup, so hiding it
        // behind a closed header would be hiding the first thing anyone has to touch.
        if (!ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var stepScale = this.configuration.StepScale;
        if (ImGui.SliderFloat("Step scale", ref stepScale, 0.01f, 2.0f, "%.3f"))
        {
            this.configuration.StepScale = stepScale;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Leave at 1.000.\n\n" +
                "The add-on reprojects each frame assuming the camera moved exactly as\n" +
                "far as it asked, so anything else puts the camera where the shader does\n" +
                "not think it is, and its focus control runs out of range before it can\n" +
                "reach the subject.\n\n" +
                "To change how far the camera travels, use the add-on's own blur radius.");
        }

        if (Math.Abs(this.configuration.StepScale - 1.0f) > 0.001f)
        {
            ImGui.TextColored(Bad, "Step scale is not 1.0 -- focus may be unreachable.");
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

        var invert = this.configuration.InvertStepDirection;
        if (ImGui.Checkbox("Invert step direction", ref invert))
        {
            this.configuration.InvertStepDirection = invert;
            this.saveConfiguration();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Only if the add-on focuses on the background when asked to focus on\n" +
                "the foreground. That is what a mirrored step direction looks like.");
        }

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
}
