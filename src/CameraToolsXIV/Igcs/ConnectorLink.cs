using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>Add-ons load independently of us, so rescan until one appears.</summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// One connected add-on.
    /// </summary>
    /// <param name="PinnedHandle">
    /// A reference taken on the module purely to keep it mapped. The buffer we write to
    /// was allocated by that module's CRT, so if ReShade unloads the add-on -- which it
    /// can do at runtime, from its own add-on list -- the allocation dies with it and our
    /// pointer dangles. Holding a reference means the module stays mapped until we let go.
    /// </param>
    private sealed record Connection(string ModuleName, nint Buffer, nint PinnedHandle);

    private readonly IPluginLog log;
    private readonly Stopwatch sinceLastScan = Stopwatch.StartNew();
    private readonly List<Connection> connections = [];

    private bool scanned;
    private bool layoutVerified;
    private bool layoutValid;

    public ConnectorLink(IPluginLog log) => this.log = log;

    public bool Connected => this.connections.Count > 0;

    public string? ConnectedModule =>
        this.connections.Count == 0 ? null : string.Join(", ", this.connections.ConvertAll(c => c.ModuleName));

    /// <summary>Looks for connectable add-ons, rate-limited so it can be called every frame.</summary>
    /// <remarks>
    /// Scanning stops once anything is found. Enumerating every loaded module is expensive
    /// enough to show up as a frame hitch, and add-ons are loaded by ReShade at startup, so
    /// a single successful scan sees all of them.
    /// </remarks>
    public void TryConnect()
    {
        if (this.scanned || this.sinceLastScan.Elapsed < RescanInterval)
        {
            return;
        }

        this.sinceLastScan.Restart();

        if (!this.VerifyLayout())
        {
            return;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                this.TryConnectModule(module);
            }
        }
        catch (Exception ex)
        {
            // Module enumeration races with libraries loading and unloading; a failed scan
            // is not fatal, the next one will pick the add-on up.
            this.log.Debug(ex, "Module scan for a camera tools connector failed.");
            return;
        }

        if (this.connections.Count > 0)
        {
            this.scanned = true;
        }
    }

    private void TryConnectModule(ProcessModule module)
    {
        var handle = module.BaseAddress;
        if (handle == nint.Zero ||
            !NativeLibrary.TryGetExport(handle, ConnectExport, out var connectAddress) ||
            !NativeLibrary.TryGetExport(handle, BufferExport, out var bufferAddress))
        {
            return;
        }

        // connectFromCameraTools returns a C++ bool, i.e. a single byte.
        var connected = ((delegate* unmanaged[Cdecl]<byte>)connectAddress)();
        if (connected == 0)
        {
            this.log.Warning($"{module.ModuleName} refused the camera tools connection.");
            return;
        }

        var allocated = ((delegate* unmanaged[Cdecl]<nint>)bufferAddress)();
        if (allocated == nint.Zero)
        {
            this.log.Warning($"{module.ModuleName} returned a null camera tools buffer.");
            return;
        }

        // Take a reference before keeping the pointer, so the module cannot be unloaded
        // out from under the buffer we are about to write to every frame.
        nint pinned = nint.Zero;
        try
        {
            pinned = NativeLibrary.Load(module.FileName);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, $"Could not pin {module.ModuleName}; skipping it rather than risk a dangling buffer.");
            return;
        }

        this.connections.Add(new Connection(module.ModuleName, allocated, pinned));
        this.log.Information($"Connected to ReShade add-on {module.ModuleName}.");
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
        foreach (var connection in this.connections)
        {
            *(CameraToolsData*)connection.Buffer = data;
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
        foreach (var connection in this.connections)
        {
            *(CameraToolsData*)connection.Buffer = cleared;
        }
    }

    public void Dispose()
    {
        foreach (var connection in this.connections)
        {
            if (connection.PinnedHandle != nint.Zero)
            {
                NativeLibrary.Free(connection.PinnedHandle);
            }
        }

        this.connections.Clear();
    }
}
