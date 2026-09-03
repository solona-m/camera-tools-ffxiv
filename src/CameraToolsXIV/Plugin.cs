using System;
using CameraToolsXIV.Camera;
using CameraToolsXIV.Igcs;
using CameraToolsXIV.Ui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/camtools";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("CameraToolsXIV");
    private readonly Configuration configuration;
    private readonly CameraController camera;
    private readonly ScreenshotSession session;
    private readonly IgcsBridge bridge;
    private readonly ConnectorLink connector;
    private readonly MainWindow window;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Plugin>();

        this.configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.camera = new CameraController(Interop, Log);
        this.session = new ScreenshotSession(this.camera, Log);
        this.bridge = new IgcsBridge(Log);
        this.connector = new ConnectorLink(Log);

        this.bridge.Load(PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, this.session);

        this.window = new MainWindow(
            this.configuration,
            this.camera,
            this.session,
            this.bridge,
            this.connector,
            this.IsCameraAllowed,
            this.SaveConfiguration);
        this.windowSystem.AddWindow(this.window);

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenMainUi;
        Framework.Update += this.OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the camera tools window.",
        });
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.camera.EnsureHooked();
        this.connector.TryConnect();

        // Drop the override the moment we are no longer allowed to hold it, so that
        // leaving Group Pose can never strand the player with a detached camera.
        if (this.camera.Enabled && !this.IsCameraAllowed())
        {
            this.camera.Disable();
        }

        this.PublishCameraData();
    }

    /// <summary>
    /// Publishes the camera state that ReShade add-ons read every frame.
    /// </summary>
    /// <remarks>
    /// This runs unconditionally rather than only while the free camera is engaged. The
    /// add-on needs live data to show a sensible UI and to decide whether to offer a
    /// session at all; the <c>cameraEnabled</c> flag is what tells it whether the camera
    /// can actually be driven.
    /// </remarks>
    private void PublishCameraData()
    {
        if (!this.connector.Connected)
        {
            return;
        }

        var snapshot = this.camera.LastSnapshot;
        if (!snapshot.Valid)
        {
            return;
        }

        var (pitch, yaw, roll) = snapshot.Basis.ToEuler();

        var data = new CameraToolsData
        {
            CameraEnabled = (byte)(this.camera.Enabled ? 1 : 0),
            CameraMovementLocked = (byte)(this.session.Active ? 1 : 0),
            // The interface specifies degrees here while the game stores radians.
            Fov = float.RadiansToDegrees(snapshot.FovRadians),
            Coordinates = snapshot.Position,
            LookQuaternion = snapshot.Basis.ToQuaternion(),
            Up = snapshot.Basis.Up,
            Right = snapshot.Basis.Right,
            Forward = snapshot.Basis.Forward,
            Pitch = pitch,
            Yaw = yaw,
            Roll = roll,
        };

        this.connector.Publish(data);
    }

    /// <summary>Whether the free camera may be engaged in the current game state.</summary>
    public bool IsCameraAllowed()
        => this.configuration.AllowOutsideGpose || ClientState.IsGPosing;

    private void OnCommand(string command, string args) => this.OpenMainUi();

    private void OpenMainUi() => this.window.Toggle();

    public void SaveConfiguration() => PluginInterface.SavePluginConfig(this.configuration);

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);

        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenMainUi;

        this.windowSystem.RemoveAllWindows();

        // Order matters on unload: stop the add-on being able to call us, tell it the
        // camera is gone, then release the camera itself.
        this.bridge.Dispose();
        this.connector.PublishDisabled();
        this.camera.Disable();
        this.camera.Dispose();
    }
}
