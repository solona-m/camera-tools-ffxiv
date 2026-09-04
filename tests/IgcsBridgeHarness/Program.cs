// Exercises the native IGCS boundary without needing FFXIV running.
//
// This is the part of the project that cannot be checked by the compiler and is
// expensive to debug in-game: the ABI between a ReShade add-on, the native shim, and the
// managed callbacks. Marshalling mistakes here (the int-vs-uint8 return type, the
// one-byte C++ bool, the calling convention) produce plausible-looking garbage rather
// than a crash, so they are worth pinning down on the desktop.
//
// The harness plays both sides: it registers callbacks the way the plugin does, then
// discovers and calls the exports exactly the way an add-on does -- by walking the
// process's loaded modules and resolving IGCS_StartScreenshotSession by name.
//
// Registration deliberately mirrors the plugin's own mechanism -- rooted delegates
// through Marshal.GetFunctionPointerForDelegate rather than [UnmanagedCallersOnly] --
// because that is the path that actually ships. The plugin cannot use
// [UnmanagedCallersOnly]: taking a function pointer to one is unsupported from the
// collectible AssemblyLoadContext Dalamud loads plugins into.

using System.Diagnostics;
using System.Runtime.InteropServices;

var shimPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "IgcsBridge.dll");

if (!File.Exists(shimPath))
{
    Console.Error.WriteLine($"IgcsBridge.dll not found at {shimPath}");
    Console.Error.WriteLine("Build it first: build/build.ps1");
    return 2;
}

var failures = 0;

void Check(string what, bool ok, string? detail = null)
{
    if (ok)
    {
        Console.WriteLine($"  PASS  {what}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL  {what}{(detail is null ? "" : $" -- {detail}")}");
    }
}

Console.WriteLine($"Loading {shimPath}");
var module = NativeLibrary.Load(shimPath);

// --- The plugin's side: register managed callbacks --------------------------------

// Rooted for the lifetime of the process: the native side keeps raw pointers to these,
// so letting them be collected would leave it calling into freed memory.
var startCallback = new StartScreenshotSessionDelegate(Recorder.OnStart);
var panoramaCallback = new MoveCameraPanoramaDelegate(Recorder.OnPanorama);
var multishotCallback = new MoveCameraMultishotDelegate(Recorder.OnMultishot);
var endCallback = new EndScreenshotSessionDelegate(Recorder.OnEnd);

unsafe
{
    var register = (delegate* unmanaged[Cdecl]<Callbacks*, void>)
        NativeLibrary.GetExport(module, "IGCSBRIDGE_Register");

    var callbacks = new Callbacks
    {
        StartScreenshotSession = Marshal.GetFunctionPointerForDelegate(startCallback),
        MoveCameraPanorama = Marshal.GetFunctionPointerForDelegate(panoramaCallback),
        MoveCameraMultishot = Marshal.GetFunctionPointerForDelegate(multishotCallback),
        EndScreenshotSession = Marshal.GetFunctionPointerForDelegate(endCallback),
    };

    register(&callbacks);
}

// --- The add-on's side: discover by module scan, then drive the camera -------------

nint discovered = nint.Zero;
string? discoveredModule = null;

using (var process = Process.GetCurrentProcess())
{
    foreach (ProcessModule loaded in process.Modules)
    {
        if (loaded.BaseAddress != nint.Zero &&
            NativeLibrary.TryGetExport(loaded.BaseAddress, "IGCS_StartScreenshotSession", out var address))
        {
            discovered = address;
            discoveredModule = loaded.ModuleName;
            break;
        }
    }
}

Console.WriteLine();
Console.WriteLine("Add-on discovery");
Check("IGCS_StartScreenshotSession found by module scan", discovered != nint.Zero, discoveredModule);

if (discovered == nint.Zero)
{
    return 1;
}

Console.WriteLine();
Console.WriteLine("Call round trip");

unsafe
{
    var start = (delegate* unmanaged[Cdecl]<byte, int>)discovered;
    var multishot = (delegate* unmanaged[Cdecl]<float, float, float, byte, void>)
        NativeLibrary.GetExport(module, "IGCS_MoveCameraMultishot");
    var panorama = (delegate* unmanaged[Cdecl]<float, void>)
        NativeLibrary.GetExport(module, "IGCS_MoveCameraPanorama");
    var end = (delegate* unmanaged[Cdecl]<void>)
        NativeLibrary.GetExport(module, "IGCS_EndScreenshotSession");

    // Session type 1 is MultiShot, the mode depth of field uses.
    var startResult = start(1);
    Check("StartScreenshotSession returns the callback's value", startResult == 0, $"got {startResult}");
    Check("session type survives marshalling", Recorder.LastType == 1, $"got {Recorder.LastType}");

    // A distinctive set of values, including a negative, so a swapped or truncated
    // argument shows up rather than coincidentally matching.
    multishot(-1.25f, 2.5f, 42.5f, 1);
    Check("multishot left/right", Recorder.LastLeftRight == -1.25f, $"got {Recorder.LastLeftRight}");
    Check("multishot up/down", Recorder.LastUpDown == 2.5f, $"got {Recorder.LastUpDown}");
    Check("multishot fov", Recorder.LastFov == 42.5f, $"got {Recorder.LastFov}");
    Check("multishot fromStartPosition", Recorder.LastFromStart == 1, $"got {Recorder.LastFromStart}");

    multishot(0f, 0f, 0f, 0);
    Check("fromStartPosition false marshals as 0", Recorder.LastFromStart == 0, $"got {Recorder.LastFromStart}");

    panorama(0.75f);
    Check("panorama step angle", Recorder.LastAngle == 0.75f, $"got {Recorder.LastAngle}");

    end();
    Check("end session invoked", Recorder.EndCount == 1, $"got {Recorder.EndCount}");

    // --- Unregistration must make the exports inert, not dangling ------------------

    var unregister = (delegate* unmanaged[Cdecl]<void>)
        NativeLibrary.GetExport(module, "IGCSBRIDGE_Unregister");
    unregister();

    var afterUnregister = start(1);

    // 4 is Error_CameraFeatureNotAvailable: the add-on's "no camera tool here" path.
    Check("start after unregister reports feature unavailable", afterUnregister == 4, $"got {afterUnregister}");
    Check("callbacks stop firing after unregister", Recorder.LastType == 1, $"got {Recorder.LastType}");

    end();
    Check("end after unregister is a no-op", Recorder.EndCount == 1, $"got {Recorder.EndCount}");
}

GC.KeepAlive(startCallback);
GC.KeepAlive(panoramaCallback);
GC.KeepAlive(multishotCallback);
GC.KeepAlive(endCallback);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int StartScreenshotSessionDelegate(byte type);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void MoveCameraPanoramaDelegate(float stepAngle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void MoveCameraMultishotDelegate(float stepLeftRight, float stepUpDown, float fovDegrees, byte fromStartPosition);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void EndScreenshotSessionDelegate();

[StructLayout(LayoutKind.Sequential)]
struct Callbacks
{
    public nint StartScreenshotSession;
    public nint MoveCameraPanorama;
    public nint MoveCameraMultishot;
    public nint EndScreenshotSession;
}

static class Recorder
{
    public static byte LastType;
    public static float LastLeftRight;
    public static float LastUpDown;
    public static float LastFov;
    public static byte LastFromStart;
    public static float LastAngle;
    public static int EndCount;

    public static int OnStart(byte type)
    {
        LastType = type;
        return 0;
    }

    public static void OnPanorama(float stepAngle) => LastAngle = stepAngle;

    public static void OnMultishot(float leftRight, float upDown, float fov, byte fromStart)
    {
        LastLeftRight = leftRight;
        LastUpDown = upDown;
        LastFov = fov;
        LastFromStart = fromStart;
    }

    public static void OnEnd() => EndCount++;
}
