using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// Loads the native IgcsBridge shim and points its exports at this plugin.
/// </summary>
/// <remarks>
/// ReShade add-ons find a camera tool by walking the process's loaded modules and calling
/// <c>GetProcAddress</c> for <c>IGCS_StartScreenshotSession</c>. A managed assembly has no
/// PE export table, so the shim exists purely to be findable. Once it is loaded anywhere
/// in the process the add-on will see it, regardless of the path it was loaded from.
/// </remarks>
internal sealed unsafe class IgcsBridge : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public delegate* unmanaged[Cdecl]<byte, int> StartScreenshotSession;
        public delegate* unmanaged[Cdecl]<float, void> MoveCameraPanorama;
        public delegate* unmanaged[Cdecl]<float, float, float, byte, void> MoveCameraMultishot;
        public delegate* unmanaged[Cdecl]<void> EndScreenshotSession;
    }

    // ScreenshotSessionStartReturnCode, from IgcsConnector's ConstantsEnums.h.
    private const int ErrorCameraFeatureNotAvailable = 4;
    private const int ErrorUnknownError = 5;

    /// <summary>
    /// The live session handler. Static because the callbacks below must be
    /// <see cref="UnmanagedCallersOnlyAttribute"/>, which forbids instance methods.
    /// </summary>
    private static ScreenshotSession? session;

    private static IPluginLog? staticLog;

    private readonly IPluginLog log;
    private nint module;

    public IgcsBridge(IPluginLog log)
    {
        this.log = log;
        staticLog = log;
    }

    public bool Loaded => this.module != nint.Zero;

    public string? LoadError { get; private set; }

    public bool Load(string pluginDirectory, ScreenshotSession sessionHandler)
    {
        if (this.Loaded)
        {
            return true;
        }

        var path = Path.Combine(pluginDirectory, "IgcsBridge.dll");
        if (!File.Exists(path))
        {
            this.LoadError = $"IgcsBridge.dll not found at {path}. Build src/native/IgcsBridge.";
            this.log.Error(this.LoadError);
            return false;
        }

        this.module = NativeLibrary.Load(path);
        if (this.module == nint.Zero)
        {
            this.LoadError = "LoadLibrary failed for IgcsBridge.dll.";
            this.log.Error(this.LoadError);
            return false;
        }

        if (!NativeLibrary.TryGetExport(this.module, "IGCSBRIDGE_Register", out var registerAddress))
        {
            this.LoadError = "IgcsBridge.dll is missing IGCSBRIDGE_Register; it is stale or not our build.";
            this.log.Error(this.LoadError);
            return false;
        }

        session = sessionHandler;

        var callbacks = new Callbacks
        {
            StartScreenshotSession = &OnStartScreenshotSession,
            MoveCameraPanorama = &OnMoveCameraPanorama,
            MoveCameraMultishot = &OnMoveCameraMultishot,
            EndScreenshotSession = &OnEndScreenshotSession,
        };

        ((delegate* unmanaged[Cdecl]<Callbacks*, void>)registerAddress)(&callbacks);
        this.log.Information("IgcsBridge loaded; IGCS exports are live.");
        return true;
    }

    // --- Entry points invoked by the ReShade add-on, on the render thread ---------------
    //
    // These must never throw: an exception crossing back into native ReShade code takes
    // the game down. Each one catches everything and degrades to an error code or a no-op.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStartScreenshotSession(byte type)
    {
        try
        {
            return session?.Start(type) ?? ErrorCameraFeatureNotAvailable;
        }
        catch (Exception ex)
        {
            staticLog?.Error(ex, "IGCS_StartScreenshotSession failed.");
            return ErrorUnknownError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMoveCameraPanorama(float stepAngle)
    {
        try
        {
            session?.MovePanorama(stepAngle);
        }
        catch (Exception ex)
        {
            staticLog?.Error(ex, "IGCS_MoveCameraPanorama failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMoveCameraMultishot(float stepLeftRight, float stepUpDown, float fovDegrees, byte fromStartPosition)
    {
        try
        {
            session?.MoveMultishot(stepLeftRight, stepUpDown, fovDegrees, fromStartPosition != 0);
        }
        catch (Exception ex)
        {
            staticLog?.Error(ex, "IGCS_MoveCameraMultishot failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEndScreenshotSession()
    {
        try
        {
            session?.End();
        }
        catch (Exception ex)
        {
            staticLog?.Error(ex, "IGCS_EndScreenshotSession failed.");
        }
    }

    public void Dispose()
    {
        if (this.module == nint.Zero)
        {
            return;
        }

        // Unregister before dropping the session: the shim's exclusive lock waits for any
        // in-flight add-on call to return, so the render thread cannot be inside one of
        // our callbacks by the time this returns. Skipping it would leave the add-on
        // calling into unloaded managed code on the next plugin reload.
        if (NativeLibrary.TryGetExport(this.module, "IGCSBRIDGE_Unregister", out var unregisterAddress))
        {
            ((delegate* unmanaged[Cdecl]<void>)unregisterAddress)();
        }

        session = null;
        staticLog = null;

        // The shim is deliberately left loaded. ReShade add-ons cache the resolved
        // function pointers, so unloading the module out from under them would leave
        // dangling pointers that outlive this plugin.
        this.module = nint.Zero;
    }
}
