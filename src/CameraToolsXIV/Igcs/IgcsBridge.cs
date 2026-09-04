using System;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// Loads the native IgcsBridge shim and points its exports at this plugin.
/// </summary>
/// <remarks>
/// <para>
/// ReShade add-ons find a camera tool by walking the process's loaded modules and calling
/// <c>GetProcAddress</c> for <c>IGCS_StartScreenshotSession</c>. A managed assembly has no
/// PE export table, so the shim exists purely to be findable. Once it is loaded anywhere
/// in the process the add-on will see it, regardless of the path it was loaded from.
/// </para>
/// <para>
/// <b>Call <see cref="Load"/> from the framework thread, never from the plugin
/// constructor.</b> Dalamud constructs plugins on its own load thread while holding
/// assembly-loading locks, and taking the Windows loader lock underneath those deadlocks
/// the process: the plugin never finishes loading and every other plugin's frame work
/// slows down as it contends for the same lock.
/// </para>
/// </remarks>
internal sealed unsafe class IgcsBridge : IDisposable
{
    // The native side stores raw pointers to these, so the delegate instances must be
    // rooted for as long as the shim can call them -- hence the fields below.
    //
    // Deliberately not [UnmanagedCallersOnly]: taking a function pointer to such a method
    // is not supported from a collectible AssemblyLoadContext, which is exactly what
    // Dalamud loads plugins into so that they can be unloaded and reloaded.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StartScreenshotSessionDelegate(byte type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MoveCameraPanoramaDelegate(float stepAngle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MoveCameraMultishotDelegate(float stepLeftRight, float stepUpDown, float fovDegrees, byte fromStartPosition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndScreenshotSessionDelegate();

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public nint StartScreenshotSession;
        public nint MoveCameraPanorama;
        public nint MoveCameraMultishot;
        public nint EndScreenshotSession;
    }

    // ScreenshotSessionStartReturnCode, from IgcsConnector's ConstantsEnums.h.
    private const int ErrorCameraFeatureNotAvailable = 4;
    private const int ErrorUnknownError = 5;

    private readonly IPluginLog log;

    private StartScreenshotSessionDelegate? startCallback;
    private MoveCameraPanoramaDelegate? panoramaCallback;
    private MoveCameraMultishotDelegate? multishotCallback;
    private EndScreenshotSessionDelegate? endCallback;

    private ScreenshotSession? session;
    private nint module;

    public IgcsBridge(IPluginLog log) => this.log = log;

    public bool Loaded => this.module != nint.Zero;

    public string? LoadError { get; private set; }

    /// <summary>Loads the shim and registers our callbacks. Framework thread only.</summary>
    public bool Load(string pluginDirectory, ScreenshotSession sessionHandler)
    {
        if (this.Loaded)
        {
            return true;
        }

        var path = Path.Combine(pluginDirectory, "IgcsBridge.dll");
        if (!File.Exists(path))
        {
            this.Fail($"IgcsBridge.dll not found at {path}. Build src/native/IgcsBridge.");
            return false;
        }

        try
        {
            this.module = NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            this.Fail($"Failed to load IgcsBridge.dll: {ex.Message}");
            return false;
        }

        if (!NativeLibrary.TryGetExport(this.module, "IGCSBRIDGE_Register", out var registerAddress))
        {
            this.Fail("IgcsBridge.dll is missing IGCSBRIDGE_Register; it is stale or not our build.");
            return false;
        }

        this.session = sessionHandler;

        this.startCallback = this.OnStartScreenshotSession;
        this.panoramaCallback = this.OnMoveCameraPanorama;
        this.multishotCallback = this.OnMoveCameraMultishot;
        this.endCallback = this.OnEndScreenshotSession;

        var callbacks = new Callbacks
        {
            StartScreenshotSession = Marshal.GetFunctionPointerForDelegate(this.startCallback),
            MoveCameraPanorama = Marshal.GetFunctionPointerForDelegate(this.panoramaCallback),
            MoveCameraMultishot = Marshal.GetFunctionPointerForDelegate(this.multishotCallback),
            EndScreenshotSession = Marshal.GetFunctionPointerForDelegate(this.endCallback),
        };

        ((delegate* unmanaged[Cdecl]<Callbacks*, void>)registerAddress)(&callbacks);
        this.log.Information("IgcsBridge loaded; IGCS exports are live.");
        return true;
    }

    private void Fail(string message)
    {
        this.LoadError = message;
        this.log.Error(message);
    }

    // --- Entry points invoked by the ReShade add-on, on the render thread ---------------
    //
    // These must never throw: an exception crossing back into native ReShade code takes
    // the game down. Each one catches everything and degrades to an error code or a no-op.

    private int OnStartScreenshotSession(byte type)
    {
        try
        {
            return this.session?.Start(type) ?? ErrorCameraFeatureNotAvailable;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "IGCS_StartScreenshotSession failed.");
            return ErrorUnknownError;
        }
    }

    private void OnMoveCameraPanorama(float stepAngle)
    {
        try
        {
            this.session?.MovePanorama(stepAngle);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "IGCS_MoveCameraPanorama failed.");
        }
    }

    private void OnMoveCameraMultishot(float stepLeftRight, float stepUpDown, float fovDegrees, byte fromStartPosition)
    {
        try
        {
            this.session?.MoveMultishot(stepLeftRight, stepUpDown, fovDegrees, fromStartPosition != 0);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "IGCS_MoveCameraMultishot failed.");
        }
    }

    private void OnEndScreenshotSession()
    {
        try
        {
            this.session?.End();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "IGCS_EndScreenshotSession failed.");
        }
    }

    public void Dispose()
    {
        if (this.module == nint.Zero)
        {
            return;
        }

        // Unregister before dropping the delegates: the shim's exclusive lock waits for
        // any in-flight add-on call to return, so the render thread cannot be inside one
        // of our callbacks by the time this comes back. Skipping it would leave the
        // add-on holding pointers into a load context that is about to be unloaded.
        if (NativeLibrary.TryGetExport(this.module, "IGCSBRIDGE_Unregister", out var unregisterAddress))
        {
            ((delegate* unmanaged[Cdecl]<void>)unregisterAddress)();
        }

        this.session = null;
        this.startCallback = null;
        this.panoramaCallback = null;
        this.multishotCallback = null;
        this.endCallback = null;

        // The shim is deliberately left loaded. ReShade add-ons cache the resolved
        // function pointers, so unloading the module out from under them would leave
        // dangling pointers that outlive this plugin.
        this.module = nint.Zero;
    }
}
