using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// Publishes live camera data into every connected ReShade add-on's shared buffer.
/// </summary>
/// <remarks>
/// Both Otis_Inf's <c>IgcsConnector.addon64</c> and Marty McFly's
/// <c>MartysMods_ParallaxDOF.addon64</c> export <c>connectFromCameraTools</c> (which
/// allocates an 8 KB buffer) and <c>getDataFromCameraToolsBuffer</c> (which hands it
/// back). Matching on the export rather than on a module name means we connect to
/// whichever of them the user has installed, and to anything else implementing the same
/// interface, without a hardcoded list.
/// </remarks>
internal sealed unsafe class ConnectorLink : IDisposable
{
    private const string ConnectExport = "connectFromCameraTools";
    private const string BufferExport = "getDataFromCameraToolsBuffer";

    /// <summary>
    /// How often to look for add-ons.
    /// </summary>
    /// <remarks>
    /// Scanning never stops. ReShade loads its add-ons in its own order and can load them
    /// after we start, so latching on the first success would connect to whichever add-on
    /// happened to be up at that instant and silently ignore the rest -- non-deterministic
    /// between launches. Rescanning is affordable because the scan below does not build
    /// managed module objects.
    /// </remarks>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One connected add-on.
    /// </summary>
    /// <param name="PinnedHandle">
    /// A reference taken on the module purely to keep it mapped. The buffer we write to
    /// was allocated by that module's CRT, so if ReShade unloads the add-on -- which it
    /// can do at runtime, from its own add-on list -- the allocation dies with it and our
    /// pointer dangles. Holding a reference means the module stays mapped until we let go.
    /// A connection is never recorded without one.
    /// </param>
    private sealed record Connection(string ModuleName, nint Buffer, nint PinnedHandle);

    private readonly IPluginLog log;
    private readonly Stopwatch sinceLastScan = Stopwatch.StartNew();

    /// <summary>
    /// Guards <see cref="connections"/> and <see cref="examined"/>.
    /// </summary>
    /// <remarks>
    /// Publishing runs on the game thread from the camera update hook, while disposal runs
    /// on Dalamud's unload thread. Without this, disposal can free a module and clear the
    /// list while a publish is part-way through iterating it -- writing 84 bytes into a
    /// module that has just been unmapped.
    /// </remarks>
    private readonly object gate = new();

    private readonly List<Connection> connections = [];

    /// <summary>
    /// Modules already dealt with, whether they connected or refused.
    /// </summary>
    /// <remarks>
    /// Refusals are remembered too. Scanning never stops, so without this a module that
    /// exports the interface but declines would be called into and warned about on every
    /// scan for the life of the session.
    /// </remarks>
    private readonly HashSet<nint> examined = [];

    private nint[] moduleBuffer = new nint[1024];

    private volatile bool connected;
    private volatile string? connectedModule;
    private volatile bool disposed;
    private bool layoutVerified;
    private bool layoutValid;
    private bool enumerationFailureLogged;

    public ConnectorLink(IPluginLog log) => this.log = log;

    public bool Connected => this.connected;

    /// <summary>Names of the connected add-ons, composed once per connection.</summary>
    public string? ConnectedModule => this.connectedModule;

    /// <summary>Looks for connectable add-ons, rate-limited so it can be called every frame.</summary>
    public void TryConnect()
    {
        if (this.disposed || this.sinceLastScan.Elapsed < RescanInterval)
        {
            return;
        }

        this.sinceLastScan.Restart();

        if (!this.VerifyLayout())
        {
            return;
        }

        // Scanning touches foreign code and races with libraries loading and unloading, so
        // it must not be able to throw into the framework update. A failed scan is not
        // fatal; the next one will pick the add-on up.
        try
        {
            var count = this.EnumerateModules();
            for (var i = 0; i < count; i++)
            {
                this.TryConnectModule(this.moduleBuffer[i]);
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Module scan for a camera tools connector failed.");
        }
    }

    /// <summary>
    /// Fills <see cref="moduleBuffer"/> with loaded module handles and returns the count.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Process.Modules</c>: that materialises a ProcessModule per
    /// entry, each of which queries the file path and version info, and measured as a
    /// frame hitch of well over a hundred milliseconds in this process. This returns bare
    /// handles, which is all the export lookup needs. The buffer is reused across scans
    /// because scanning continues for the life of the session.
    /// </remarks>
    private int EnumerateModules()
    {
        while (true)
        {
            var sizeBytes = (uint)(this.moduleBuffer.Length * IntPtr.Size);
            if (!K32EnumProcessModules(GetCurrentProcess(), this.moduleBuffer, sizeBytes, out var needed))
            {
                // A silent total failure here would look exactly like "no add-on
                // installed" and send the user to check their ReShade setup instead.
                if (!this.enumerationFailureLogged)
                {
                    this.enumerationFailureLogged = true;
                    this.log.Error(
                        $"EnumProcessModules failed (error {Marshal.GetLastWin32Error()}); " +
                        "cannot find ReShade add-ons.");
                }

                return 0;
            }

            var count = (int)(needed / IntPtr.Size);
            if (count <= this.moduleBuffer.Length)
            {
                return count;
            }

            this.moduleBuffer = new nint[count];
        }
    }

    private void TryConnectModule(nint handle)
    {
        if (handle == nint.Zero)
        {
            return;
        }

        lock (this.gate)
        {
            if (this.disposed || !this.examined.Add(handle))
            {
                return;
            }
        }

        // Everything below runs outside the lock: it calls into the add-on, and holding a
        // lock the per-frame publish path also takes while foreign code runs would stall
        // the game thread. Only one thread ever scans, so claiming the handle above is
        // enough to keep this from overlapping with itself.
        if (!NativeLibrary.TryGetExport(handle, ConnectExport, out var connectAddress) ||
            !NativeLibrary.TryGetExport(handle, BufferExport, out var bufferAddress))
        {
            return;
        }

        var path = GetModulePath(handle);
        var name = path.Length == 0 ? $"0x{handle:X}" : System.IO.Path.GetFileName(path);

        // Without a path we cannot take a reference, and without a reference the buffer
        // can be freed under us while we are still writing to it every frame. Refuse the
        // module rather than connect to one we cannot keep alive.
        if (path.Length == 0)
        {
            this.log.Warning($"Could not resolve a path for module {name}; skipping it rather than risk a dangling buffer.");
            return;
        }

        // connectFromCameraTools returns a C++ bool, i.e. a single byte.
        var accepted = ((delegate* unmanaged[Cdecl]<byte>)connectAddress)();
        if (accepted == 0)
        {
            this.log.Warning($"{name} refused the camera tools connection.");
            return;
        }

        var allocated = ((delegate* unmanaged[Cdecl]<nint>)bufferAddress)();
        if (allocated == nint.Zero)
        {
            this.log.Warning($"{name} returned a null camera tools buffer.");
            return;
        }

        nint pinned;
        try
        {
            pinned = NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, $"Could not pin {name}; skipping it rather than risk a dangling buffer.");
            return;
        }

        if (pinned == nint.Zero)
        {
            this.log.Warning($"Pinning {name} returned no handle; skipping it rather than risk a dangling buffer.");
            return;
        }

        lock (this.gate)
        {
            if (this.disposed)
            {
                NativeLibrary.Free(pinned);
                return;
            }

            this.connections.Add(new Connection(name, allocated, pinned));
            this.connectedModule = string.Join(", ", this.connections.ConvertAll(c => c.ModuleName));
            this.connected = true;
        }

        this.log.Information($"Connected to ReShade add-on {name}.");
    }

    /// <summary>
    /// Checks that our struct matches the byte layout the add-ons expect.
    /// </summary>
    /// <remarks>
    /// The struct is written straight over the add-on's buffer, so its layout has to match
    /// IgcsConnector's C++ definition exactly. A mismatch would not crash: every field
    /// after the first bad offset would simply be read as something else, and the add-on
    /// would show plausible-looking nonsense. Better to refuse to publish and say why.
    /// </remarks>
    private bool VerifyLayout()
    {
        if (this.layoutVerified)
        {
            return this.layoutValid;
        }

        this.layoutVerified = true;
        var actual = sizeof(CameraToolsData);
        this.layoutValid = actual == CameraToolsData.ExpectedSizeBytes;

        if (!this.layoutValid)
        {
            this.log.Error(
                $"CameraToolsData is {actual} bytes but the IGCS interface expects " +
                $"{CameraToolsData.ExpectedSizeBytes}. Refusing to publish camera data.");
        }

        return this.layoutValid;
    }

    /// <summary>Writes the current camera state into every connected add-on's buffer.</summary>
    public void Publish(in CameraToolsData data)
    {
        lock (this.gate)
        {
            foreach (var connection in this.connections)
            {
                *(CameraToolsData*)connection.Buffer = data;
            }
        }
    }

    /// <summary>
    /// Marks the camera as disabled in every connected buffer.
    /// </summary>
    /// <remarks>
    /// Called on unload. The buffers belong to the add-ons and outlive us, so leaving a
    /// stale "camera enabled" flag behind would make an add-on offer a depth-of-field
    /// session that nothing is listening for.
    /// </remarks>
    public void PublishDisabled()
    {
        var cleared = default(CameraToolsData);
        lock (this.gate)
        {
            foreach (var connection in this.connections)
            {
                *(CameraToolsData*)connection.Buffer = cleared;
            }
        }
    }

    public void Dispose()
    {
        List<nint> toFree;

        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.connected = false;
            this.connectedModule = null;

            // Collect the handles and drop the connections inside the lock, so a publish
            // in flight on the game thread has finished before any buffer stops being
            // written to and before any module is released.
            toFree = this.connections.ConvertAll(c => c.PinnedHandle);
            this.connections.Clear();
            this.examined.Clear();
        }

        // Freeing happens outside the lock. FreeLibrary runs the module's detach path and
        // takes the Windows loader lock; doing that while holding a lock the game thread
        // takes every frame would stall it, and orders our lock before the loader lock.
        foreach (var handle in toFree)
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>Resolves a module's full path, growing the buffer until it fits.</summary>
    /// <remarks>
    /// GetModuleFileNameW returns the buffer size on truncation rather than failing, so a
    /// fixed MAX_PATH buffer silently clips long paths -- which then surfaces as a
    /// misleading "could not pin" for a path that was simply cut short.
    /// </remarks>
    private static string GetModulePath(nint module)
    {
        var capacity = 260;

        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetModuleFileNameW(module, buffer, (uint)capacity);

            if (length == 0)
            {
                return string.Empty;
            }

            if (length < capacity)
            {
                return buffer.ToString(0, (int)length);
            }

            capacity *= 2;
        }

        return string.Empty;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumProcessModules(nint hProcess, [Out] nint[] lphModule, uint cb, out uint lpcbNeeded);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint hModule, StringBuilder lpFilename, uint nSize);
}
