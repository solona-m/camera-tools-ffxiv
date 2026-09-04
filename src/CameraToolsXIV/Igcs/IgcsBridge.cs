using System;
using System.Diagnostics;
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

    /// <summary>Shim contract this build understands. See IGCSBRIDGE_GetVersion.</summary>
    private const uint RequiredShimVersion = 2;

    private ScreenshotSession? session;
    private nint module;
    private ulong token;

    public IgcsBridge(IPluginLog log) => this.log = log;

    /// <summary>
    /// Whether the shim is loaded <b>and</b> our callbacks are registered with it. A shim
    /// that mapped but would not accept a registration does not count as loaded: it cannot
    /// serve an add-on, so reporting it as working would be a lie in the one place someone
    /// looks to find out.
    /// </summary>
    public bool Loaded => this.module != nint.Zero;

    public string? LoadError { get; private set; }

    /// <summary>Loads the shim and registers our callbacks. Framework thread only.</summary>
    public bool Load(string pluginDirectory, ScreenshotSession sessionHandler)
    {
        if (this.Loaded)
        {
            return true;
        }

        // Reuse a shim already mapped into this process before loading another.
        //
        // Windows keys modules on path, so a dev build and an installed build would map two
        // distinct IgcsBridge.dlls. An add-on binds to whichever exports the symbol first
        // and never re-checks, so when one plugin unloads its shim keeps answering "no
        // camera tool" for the rest of the session while the other instance sits there
        // working and unused. One shared shim, with the registration token deciding who
        // owns it, makes two live instances harmless.
        this.module = FindLoadedShim(this.log);

        if (this.module != nint.Zero)
        {
            this.log.Information("Reusing an IgcsBridge already loaded in this process.");
        }
        else
        {
            var source = Path.Combine(pluginDirectory, "IgcsBridge.dll");
            if (!File.Exists(source))
            {
                this.Fail($"IgcsBridge.dll not found at {source}. Build src/native/IgcsBridge.");
                return false;
            }

            // Load a copy, never the file in the plugin directory.
            //
            // A mapped DLL is locked for as long as it stays mapped, and this one is never
            // unmapped -- add-ons cache pointers into it, so unloading would leave them
            // dangling. For a dev plugin the plugin directory IS the build output, so
            // loading in place means the game holds the build's own IgcsBridge.dll open and
            // every rebuild fails on a file-copy error until the game is closed.
            var path = CopyForLoading(source, this.log) ?? source;

            try
            {
                this.module = NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                this.Fail($"Failed to load IgcsBridge.dll: {ex.Message}");
                return false;
            }
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

        this.token = ((delegate* unmanaged[Cdecl]<Callbacks*, ulong>)registerAddress)(&callbacks);
        if (this.token == 0)
        {
            this.Fail("IgcsBridge refused the registration.");
            return false;
        }

        this.log.Information($"IgcsBridge loaded; IGCS exports are live (registration {this.token}).");
        return true;
    }

    /// <summary>
    /// Finds a compatible shim already mapped into this process, or zero if there is none.
    /// </summary>
    /// <remarks>
    /// Version 2 introduced the registration token. An older shim cannot be shared, because
    /// its Unregister clears unconditionally, so an older instance unloading would take
    /// this one down with it. Mapping our own and accepting the duplicate is the lesser
    /// problem.
    /// </remarks>
    private static nint FindLoadedShim(IPluginLog log)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var handle = module.BaseAddress;
                if (handle == nint.Zero ||
                    !NativeLibrary.TryGetExport(handle, "IGCSBRIDGE_GetVersion", out var versionAddress) ||
                    !NativeLibrary.TryGetExport(handle, "IGCSBRIDGE_Register", out _))
                {
                    continue;
                }

                var version = ((delegate* unmanaged[Cdecl]<uint>)versionAddress)();
                if (version >= RequiredShimVersion)
                {
                    return handle;
                }

                log.Warning($"An IgcsBridge of version {version} is loaded and cannot be shared; loading our own.");
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not scan for an existing IgcsBridge.");
        }

        return nint.Zero;
    }

    /// <summary>
    /// Copies the shim somewhere disposable and returns that path, or null to load in place.
    /// </summary>
    /// <remarks>
    /// The copy is what gets locked, leaving the build output free to be overwritten while
    /// the game runs. Names are unique per load because a previous copy may still be mapped
    /// from an earlier plugin reload; stale ones from previous sessions are swept up on the
    /// way past, and any that are still mapped simply refuse to delete.
    /// </remarks>
    private static string? CopyForLoading(string source, IPluginLog log)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "CameraToolsXIV");
            Directory.CreateDirectory(directory);

            foreach (var stale in Directory.EnumerateFiles(directory, "IgcsBridge-*.dll"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (IOException)
                {
                    // Still mapped by this process or another. It will go on the next run.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            var path = Path.Combine(directory, $"IgcsBridge-{Guid.NewGuid():N}.dll");
            File.Copy(source, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            // Falling back to loading in place still works; it just holds the build output
            // open, which matters only on a developer's machine.
            log.Warning(ex, "Could not stage a copy of IgcsBridge.dll; loading it in place.");
            return null;
        }
    }

    /// <summary>
    /// Records why loading failed and makes sure <see cref="Loaded"/> reports it.
    /// </summary>
    /// <remarks>
    /// Clearing the handle here rather than at each call site is deliberate. The module is
    /// assigned before registration is attempted, so a failure after that point would
    /// otherwise leave Loaded true: the window would show "IGCS exports active" in green,
    /// the branch that displays LoadError would be unreachable, and the plugin would look
    /// perfectly healthy while no callbacks had been registered at all. That is exactly the
    /// green-but-inert failure this status line exists to rule out.
    /// <para>
    /// A module we mapped stays mapped — add-ons cache pointers into it — so forgetting the
    /// handle is not a leak of anything reclaimable, and a later attempt finds it again
    /// through <see cref="FindLoadedShim"/>.
    /// </para>
    /// </remarks>
    private void Fail(string message)
    {
        this.LoadError = message;
        this.module = nint.Zero;
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
        // Passing our token means this clears the callbacks only if we still own them. If
        // another copy of the plugin has registered since, it keeps working instead of
        // being silently disconnected by our unload.
        if (this.token != 0 &&
            NativeLibrary.TryGetExport(this.module, "IGCSBRIDGE_Unregister", out var unregisterAddress))
        {
            ((delegate* unmanaged[Cdecl]<ulong, void>)unregisterAddress)(this.token);
        }

        this.token = 0;

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
