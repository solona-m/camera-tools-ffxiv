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
    /// </param>
    private sealed record Connection(nint BaseAddress, string ModuleName, nint Buffer, nint PinnedHandle);

    private readonly IPluginLog log;
    private readonly Stopwatch sinceLastScan = Stopwatch.StartNew();

    /// <summary>
    /// Guards <see cref="connections"/>.
    /// </summary>
    /// <remarks>
    /// Publishing runs on the game thread from the camera update hook, while disposal runs
    /// on Dalamud's unload thread. Without this, disposal can free a module and clear the
    /// list while a publish is part-way through iterating it -- writing 84 bytes into a
    /// module that has just been unmapped.
    /// </remarks>
    private readonly object gate = new();

    private readonly List<Connection> connections = [];
    private readonly HashSet<nint> connectedModules = [];

    private volatile bool connected;
    private volatile string? connectedModule;
    private bool layoutVerified;
    private bool layoutValid;
    private bool disposed;

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

        foreach (var module in EnumerateModules())
        {
            this.TryConnectModule(module);
        }
    }

    /// <summary>
    /// Enumerates loaded module handles.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Process.Modules</c>: that materialises a ProcessModule per
    /// entry, each of which queries the file path and version info, and measured as a
    /// frame hitch of well over a hundred milliseconds in this process. This returns bare
    /// handles, which is all the export lookup needs.
    /// </remarks>
    private static nint[] EnumerateModules()
    {
        var process = GetCurrentProcess();
        var handles = new nint[1024];

        while (true)
        {
            var sizeBytes = (uint)(handles.Length * IntPtr.Size);
            if (!K32EnumProcessModules(process, handles, sizeBytes, out var needed))
            {
                return [];
            }

            var count = (int)(needed / IntPtr.Size);
            if (count <= handles.Length)
            {
                Array.Resize(ref handles, count);
                return handles;
            }

            handles = new nint[count];
        }
    }

    private void TryConnectModule(nint handle)
    {
        if (handle == nint.Zero || this.connectedModules.Contains(handle))
        {
            return;
        }

        if (!NativeLibrary.TryGetExport(handle, ConnectExport, out var connectAddress) ||
            !NativeLibrary.TryGetExport(handle, BufferExport, out var bufferAddress))
        {
            return;
        }

        var path = GetModulePath(handle);
        var name = path.Length == 0 ? $"0x{handle:X}" : System.IO.Path.GetFileName(path);

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

        // Take a reference before keeping the pointer, so the module cannot be unloaded
        // out from under the buffer we are about to write to every frame.
        nint pinned = nint.Zero;
        if (path.Length > 0)
        {
            try
            {
                pinned = NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, $"Could not pin {name}; skipping it rather than risk a dangling buffer.");
                return;
            }
        }

        lock (this.gate)
        {
            if (this.disposed)
            {
                if (pinned != nint.Zero)
                {
                    NativeLibrary.Free(pinned);
                }

                return;
            }

            this.connectedModules.Add(handle);
            this.connections.Add(new Connection(handle, name, allocated, pinned));
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
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.connected = false;
            this.connectedModule = null;

            // Inside the lock: a publish in flight on the game thread must finish before
            // any module is released, or it writes into memory that has just been unmapped.
            foreach (var connection in this.connections)
            {
                if (connection.PinnedHandle != nint.Zero)
                {
                    NativeLibrary.Free(connection.PinnedHandle);
                }
            }

            this.connections.Clear();
            this.connectedModules.Clear();
        }
    }

    private static string GetModulePath(nint module)
    {
        var buffer = new StringBuilder(260);
        var length = GetModuleFileNameW(module, buffer, (uint)buffer.Capacity);
        return length == 0 ? string.Empty : buffer.ToString(0, (int)length);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumProcessModules(nint hProcess, [Out] nint[] lphModule, uint cb, out uint lpcbNeeded);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint hModule, StringBuilder lpFilename, uint nSize);
}
