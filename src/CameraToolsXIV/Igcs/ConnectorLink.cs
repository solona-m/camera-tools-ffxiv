using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// Publishes live camera data into a connected ReShade add-on's shared buffer.
/// </summary>
/// <remarks>
/// Both Otis_Inf's <c>IgcsConnector.addon64</c> and Marty McFly's
/// <c>MartysMods_ParallaxDOF.addon64</c> export <c>connectFromCameraTools</c> (which
/// allocates an 8 KB buffer) and <c>getDataFromCameraToolsBuffer</c> (which hands it
/// back). Matching on the export rather than on a module name means we connect to
/// whichever of them the user has installed, and to anything else implementing the same
/// interface, without a hardcoded list.
/// </remarks>
internal sealed unsafe class ConnectorLink
{
    private const string ConnectExport = "connectFromCameraTools";
    private const string BufferExport = "getDataFromCameraToolsBuffer";

    /// <summary>Add-ons load independently of us, so rescan periodically until one appears.</summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private readonly IPluginLog log;
    private readonly Stopwatch sinceLastScan = Stopwatch.StartNew();

    private nint buffer;

    public ConnectorLink(IPluginLog log) => this.log = log;

    public bool Connected => this.buffer != nint.Zero;

    public string? ConnectedModule { get; private set; }

    /// <summary>Looks for a connectable add-on, rate-limited so it can be called every frame.</summary>
    public void TryConnect()
    {
        if (this.Connected || this.sinceLastScan.Elapsed < RescanInterval)
        {
            return;
        }

        this.sinceLastScan.Restart();

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var handle = module.BaseAddress;
                if (handle == nint.Zero ||
                    !NativeLibrary.TryGetExport(handle, ConnectExport, out var connectAddress) ||
                    !NativeLibrary.TryGetExport(handle, BufferExport, out var bufferAddress))
                {
                    continue;
                }

                // connectFromCameraTools returns a C++ bool, i.e. a single byte.
                var connected = ((delegate* unmanaged[Cdecl]<byte>)connectAddress)();
                if (connected == 0)
                {
                    this.log.Warning($"{module.ModuleName} refused the camera tools connection.");
                    continue;
                }

                var allocated = ((delegate* unmanaged[Cdecl]<nint>)bufferAddress)();
                if (allocated == nint.Zero)
                {
                    this.log.Warning($"{module.ModuleName} returned a null camera tools buffer.");
                    continue;
                }

                this.buffer = allocated;
                this.ConnectedModule = module.ModuleName;
                this.log.Information($"Connected to ReShade add-on {module.ModuleName}.");
                return;
            }
        }
        catch (Exception ex)
        {
            // Module enumeration races with libraries loading and unloading; a failed
            // scan is not fatal, the next one will pick the add-on up.
            this.log.Debug(ex, "Module scan for a camera tools connector failed.");
        }
    }

    /// <summary>Writes the current camera state into the add-on's buffer.</summary>
    public void Publish(in CameraToolsData data)
    {
        if (!this.Connected)
        {
            return;
        }

        *(CameraToolsData*)this.buffer = data;
    }

    /// <summary>
    /// Marks the camera as disabled in the shared buffer.
    /// </summary>
    /// <remarks>
    /// Called on unload. The buffer belongs to the add-on and outlives us, so leaving a
    /// stale "camera enabled" flag behind would make the add-on offer a depth-of-field
    /// session that nothing is listening for.
    /// </remarks>
    public void PublishDisabled()
    {
        if (!this.Connected)
        {
            return;
        }

        var cleared = default(CameraToolsData);
        *(CameraToolsData*)this.buffer = cleared;
    }
}
