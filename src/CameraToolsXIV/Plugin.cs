using System;
using CameraToolsXIV.Camera;
using CameraToolsXIV.Igcs;
using CameraToolsXIV.Ui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV;

/// <summary>
/// Plugin entry point.
/// </summary>
/// <remarks>
/// The constructor does nothing but store the injected services and subscribe to the
/// framework tick. Everything else happens in <see cref="Initialize"/> on the first
/// frame, off Dalamud's plugin-load thread.
/// <para>
/// This is not stylistic. Dalamud constructs plugins on a load thread while holding
/// assembly-loading locks, so real work there deadlocks the process instead of failing:
/// the log stops at "Creating plugin instance" and the whole game degrades as other
/// plugins queue behind the held locks. Two separate instances of that were hit here --
/// taking the Windows loader lock via NativeLibrary.Load, and calling
/// <c>Create&lt;Plugin&gt;()</c>, which builds a <i>new</i> instance of the type passed to
/// it and so recurses into constructing another Plugin.
/// </para>
/// </remarks>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/camtools";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;

    private WindowSystem? windowSystem;
    private Configuration? configuration;
    private CameraController? camera;
    private ScreenshotSession? session;
    private IgcsBridge? bridge;
    private ConnectorLink? connector;
    private MainWindow? window;

    private bool initializeAttempted;
    private bool ready;
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.clientState = clientState;
        this.interop = interop;
        this.log = log;

        this.framework.Update += this.OnFrameworkUpdate;
    }

    private void Initialize()
    {
        this.configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.camera = new CameraController(this.interop, this.log);
        this.camera.TransformSettled = this.PublishCameraData;
        this.session = new ScreenshotSession(this.camera, this.configuration, this.log);
        this.bridge = new IgcsBridge(this.log);
        this.connector = new ConnectorLink(this.log);

        this.window = new MainWindow(
            this.configuration,
            this.camera,
            this.session,
            this.bridge,
            this.connector,
            this.IsCameraAllowed,
            this.SaveConfiguration);

        this.windowSystem = new WindowSystem("CameraToolsXIV");
        this.windowSystem.AddWindow(this.window);

        // Dalamud hides plugin windows during Group Pose and cutscenes by default, which
        // is exactly where this tool is used -- without these the window is invisible
        // precisely when it is needed.
        this.pluginInterface.UiBuilder.DisableGposeUiHide = true;
        this.pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenMainUi;

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the camera tools window.",
        });

        this.bridge.Load(this.pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, this.session);

        this.ready = true;
        this.log.Information("Camera Tools initialised.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.disposed)
        {
            return;
        }

        if (!this.initializeAttempted)
        {
            // Set before the attempt, not after: if initialisation throws we want one
            // logged failure, not the same exception every frame forever.
            this.initializeAttempted = true;

            try
            {
                this.Initialize();
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "Camera Tools failed to initialise.");
            }

            return;
        }

        if (!this.ready)
        {
            return;
        }

        this.camera!.EnsureHooked();
        this.connector!.TryConnect();

        // A running session is left strictly alone.
        //
        // Tearing one down because the game state blinked -- IsGPosing flickering, or a
        // dev reload -- is worse than useless: the add-on is not told, so it carries on
        // compositing frames against a camera that has silently stopped moving, and the
        // result is a ghosted image that no amount of focus tuning can fix. A stack lasts
        // seconds to minutes and the add-on owns the camera for the duration, so let it
        // finish and release the camera itself.
        if (!this.session!.Active)
        {
            this.camera.SetArmed(this.IsCameraAllowed() && this.camera.LastSnapshot.Valid);
        }

        // Camera data is published from the camera update itself, not here, so that what
        // the add-on reads always describes the frame about to be rendered.
    }

    /// <summary>
    /// Publishes the camera state that ReShade add-ons read every frame.
    /// </summary>
    /// <remarks>
    /// This runs unconditionally rather than only while the camera is armed. The add-on
    /// needs live data to show a sensible UI and to decide whether to offer a session at
    /// all; the <c>cameraEnabled</c> flag is what tells it whether the camera can
    /// actually be driven.
    /// </remarks>
    private void PublishCameraData()
    {
        if (!this.connector!.Connected)
        {
            return;
        }

        var snapshot = this.camera!.LastSnapshot;
        if (!snapshot.Valid)
        {
            return;
        }

        var (pitch, yaw, roll) = snapshot.Basis.ToEuler();

        var data = new CameraToolsData
        {
            CameraEnabled = (byte)(this.camera.Armed ? 1 : 0),
            CameraMovementLocked = (byte)(this.session!.Active ? 1 : 0),
            // The interface specifies degrees here while the game stores radians.
            Fov = float.RadiansToDegrees(
                this.configuration!.PublishHorizontalFov
                    ? snapshot.HorizontalFovRadians
                    : snapshot.FovRadians),
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

    /// <summary>Whether the camera may be taken over in the current game state.</summary>
    public bool IsCameraAllowed()
        => (this.configuration?.AllowOutsideGpose ?? false) || this.clientState.IsGPosing;

    private void DrawUi() => this.windowSystem?.Draw();

    private void OnCommand(string command, string args) => this.OpenMainUi();

    private void OpenMainUi() => this.window?.Toggle();

    public void SaveConfiguration()
    {
        if (this.configuration is not null)
        {
            this.pluginInterface.SavePluginConfig(this.configuration);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.framework.Update -= this.OnFrameworkUpdate;

        // Initialisation may never have run, or may have thrown partway through, so every
        // teardown step below has to tolerate a half-built plugin.
        if (this.initializeAttempted)
        {
            this.commandManager.RemoveHandler(CommandName);

            this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
            this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
            this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenMainUi;
        }

        this.windowSystem?.RemoveAllWindows();

        // Order matters on unload: stop the add-on being able to call us, tell it the
        // camera is gone, then release the camera itself. Aborting after the bridge is
        // disposed means no in-flight session call can re-acquire the hold behind us.
        // Detach first: this fires from the game's camera update, and must not run against
        // a connector that is being torn down underneath it.
        if (this.camera is not null)
        {
            this.camera.TransformSettled = null;
        }

        this.bridge?.Dispose();
        this.connector?.PublishDisabled();
        this.session?.Abort();
        this.camera?.SetArmed(false);
        this.camera?.Dispose();

        this.log.Information("Camera Tools unloaded.");
    }
}
