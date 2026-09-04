using System;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace CameraToolsXIV.World;

/// <summary>
/// Drives the game's logic clock to a standstill while an add-on is stacking frames, so
/// that water, foliage, weather and particles hold still along with the actors.
/// </summary>
/// <remarks>
/// <para>
/// Experimental, and off by default. Freezing bone physics fixes the worst of the ghosting
/// in a stack but leaves the environment moving, and there is no narrower lever for the
/// environment than the frame delta itself: water, wind sway, sky and VFX are all advanced
/// by it. This is the same mechanism the game uses natively when it puts up a message box
/// -- logic delta goes to nothing while rendering carries on -- which is exactly the shape
/// an accumulation stack wants.
/// </para>
/// <para>
/// It is also the bluntest thing this plugin does. Game logic stopping means chat, movement,
/// and everything else driven by the tick stop with it. Hence off by default, hence bounded
/// by the session, and hence the watchdog below.
/// </para>
/// <para>
/// All of this is game-thread only, driven from the framework tick. The value is reasserted
/// every tick rather than written once, so that something else writing the field mid-stack
/// does not silently un-pause the world partway through a render.
/// </para>
/// </remarks>
internal sealed unsafe class WorldPause : IDisposable
{
    /// <summary>The frame delta imposed while paused, in seconds.</summary>
    /// <remarks>
    /// Not zero: zero is the field's own "no override in effect" sentinel, so writing it
    /// would hand the clock straight back. Not <see cref="float.Epsilon"/> either -- that is
    /// a denormal, and code that divides by the delta deserves better than one. A microsecond
    /// is an ordinary float and adds up to well under a millisecond across an entire stack.
    /// </remarks>
    private const float PausedDelta = 1e-6f;

    /// <summary>
    /// The longest the world may stay paused, however long the session runs.
    /// </summary>
    /// <remarks>
    /// The session already bounds the pause, and "Release camera" already ends the session.
    /// This is the backstop for the case those do not cover: an add-on that dies mid-stack
    /// without ending its session, leaving the world stopped with the user unable to reach
    /// anything to fix it. A stranded pause is severe enough not to rest on one mechanism.
    /// <para>
    /// Twenty minutes, and the number is not a safety margin plucked out of the air -- it
    /// has to clear the longest stack anyone would actually render. Parallax DoF quotes
    /// three and a half minutes for 1024 samples at four accumulation frames, and both of
    /// those go higher. A watchdog that fires mid-render is worse than none: it thaws the
    /// world for the back half of a stack, so every remaining frame disagrees with every
    /// frame already composited, and the result looks like the freeze never worked.
    /// </para>
    /// </remarks>
    private const long MaxPauseMs = 20 * 60 * 1000;

    private readonly IPluginLog log;

    private bool paused;
    private float priorOverride;
    private long pausedSinceMs;

    // Set when the watchdog fires, cleared when the caller next asks for the pause to end.
    // Without it the tick would simply re-apply the pause on the following frame and the
    // watchdog would achieve nothing but a stutter.
    private bool watchdogTripped;

    public WorldPause(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>Whether the world is currently held.</summary>
    public bool Paused => this.paused;

    /// <summary>
    /// Applies or releases the pause, and reasserts it while held. Game thread only; call
    /// every tick.
    /// </summary>
    public void SetPaused(bool value)
    {
        if (!value)
        {
            this.watchdogTripped = false;
            this.Release();
            return;
        }

        if (this.watchdogTripped)
        {
            return;
        }

        this.Apply();
    }

    private void Apply()
    {
        var framework = GameFramework.Instance();
        if (framework is null)
        {
            return;
        }

        if (!this.paused)
        {
            // Whatever was there before is not assumed to be zero. Another plugin may own
            // the field, and handing back a value we invented rather than the one we found
            // would break it.
            this.priorOverride = framework->FrameDeltaTimeOverride;
            this.pausedSinceMs = Environment.TickCount64;
            this.paused = true;

            framework->FrameDeltaTimeOverride = PausedDelta;
            this.log.Information("World paused for the duration of the stack.");
            return;
        }

        if (Environment.TickCount64 - this.pausedSinceMs > MaxPauseMs)
        {
            this.watchdogTripped = true;
            this.log.Warning(
                $"World has been paused for over {MaxPauseMs / 60_000} minutes; releasing it. " +
                "The add-on most likely never ended its session.");
            this.Release();
            return;
        }

        framework->FrameDeltaTimeOverride = PausedDelta;
    }

    private void Release()
    {
        if (!this.paused)
        {
            return;
        }

        this.paused = false;

        var framework = GameFramework.Instance();
        if (framework is null)
        {
            return;
        }

        // Restore only over our own value. Anything else means something took the field
        // while we held it, and writing the old value back would be undoing its work rather
        // than our own.
        if (framework->FrameDeltaTimeOverride == PausedDelta)
        {
            framework->FrameDeltaTimeOverride = this.priorOverride;
        }
        else
        {
            this.log.Warning(
                "The frame delta override changed while the world was paused; " +
                "leaving it alone rather than overwriting whatever now owns it.");
        }
    }

    public void Dispose() => this.Release();
}
