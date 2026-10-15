using System;

namespace CameraToolsXIV.Igcs;

/// <summary>What the aperture warning should show right now.</summary>
/// <param name="Warn">Whether the add-on is asking for an aperture far too large for FFXIV.</param>
/// <param name="Step">The most recent too-large step, in world units; zero if there was none.</param>
/// <param name="FromEndedSession">Whether this describes a session that has already ended.</param>
internal readonly record struct ApertureStatus(bool Warn, float Step, bool FromEndedSession);

/// <summary>
/// Notices when an add-on is stepping the camera across an aperture far too large for
/// FFXIV, so the user can be told which setting to change.
/// </summary>
/// <remarks>
/// <para>
/// Watching is all this does. The plugin applies every step exactly as asked, because the
/// shader reprojects assuming it did, and it cannot reach the add-on's settings to fix
/// them. Parallax DoF's default Blur Radius of 1.0 is a one-metre lens in FFXIV's units,
/// which sees behind the subject and blurs it along with the background; the only remedy
/// is the user typing a smaller value, and this is how they find out.
/// </para>
/// <para>
/// Kept free of Dalamud so the harness can check it off the game. Not thread-safe: the
/// owning session serialises every call under its own lock.
/// </para>
/// </remarks>
internal sealed class ApertureWatch
{
    /// <summary>
    /// Steps longer than this, in world units, are flagged. Strictly longer: exactly ten
    /// centimetres is allowed.
    /// </summary>
    /// <remarks>
    /// Ten centimetres is already wider than any real lens's aperture radius, so a
    /// deliberately strong but physical blur stays quiet, while the 1.0 default is ten
    /// times over. Anything between that still sees around the subject.
    /// </remarks>
    public const float WarnAboveWorldUnits = 0.1f;

    /// <summary>How long a warning outlives the last too-large step during a session.</summary>
    /// <remarks>
    /// A stack samples the whole aperture, and samples near its centre are short even when
    /// the radius is far too large. Clearing on the first short step would flicker; never
    /// clearing would leave the warning red after the user fixed the radius mid-preview,
    /// which reads as the fix having done nothing.
    /// </remarks>
    public const long ClearAfterMilliseconds = 2000;

    private bool sawTooLarge;
    private long lastTooLargeAt;
    private float lastTooLargeStep;
    private float latestStep;
    private bool announced;
    private float? pendingAnnouncement;
    private bool ended;
    private bool endedWarn;

    /// <summary>Forgets everything. Called when a session starts.</summary>
    public void Reset()
    {
        this.sawTooLarge = false;
        this.lastTooLargeAt = 0;
        this.lastTooLargeStep = 0f;
        this.latestStep = 0f;
        this.announced = false;
        this.pendingAnnouncement = null;
        this.ended = false;
        this.endedWarn = false;
    }

    /// <summary>Records one step as the add-on sent it.</summary>
    public void Observe(float stepLeftRight, float stepUpDown, long nowMs)
    {
        // A non-finite step is some other fault entirely, and would turn the message
        // into nonsense rather than tell the user anything about their Blur Radius.
        if (!float.IsFinite(stepLeftRight) || !float.IsFinite(stepUpDown))
        {
            return;
        }

        // The length, not each axis: a diagonal sample can be too large while neither of
        // its components is.
        var length = MathF.Sqrt((stepLeftRight * stepLeftRight) + (stepUpDown * stepUpDown));
        this.latestStep = length;

        if (length <= WarnAboveWorldUnits)
        {
            return;
        }

        this.sawTooLarge = true;
        this.lastTooLargeAt = nowMs;
        this.lastTooLargeStep = length;

        // The first too-large step, not the largest: the tick takes this within a frame,
        // long before the add-on sends another step, so the two do not differ in practice.
        if (!this.announced)
        {
            this.announced = true;
            this.pendingAnnouncement = length;
        }
    }

    /// <summary>
    /// Freezes the verdict as it stands. Called when a session ends or is released.
    /// </summary>
    /// <remarks>
    /// Only the first call counts. After "Release camera" the add-on still believes it
    /// holds the camera and sends its own end later; re-judging then, long after the last
    /// step, would quietly clear the warning that explains why the stack came out blurred.
    /// </remarks>
    public void SessionEnded(long nowMs)
    {
        if (this.ended)
        {
            return;
        }

        this.endedWarn = this.RunningWarn(nowMs);
        this.ended = true;
    }

    public ApertureStatus Status(long nowMs)
    {
        var warn = this.ended ? this.endedWarn : this.RunningWarn(nowMs);
        return new ApertureStatus(warn, this.lastTooLargeStep, this.ended);
    }

    /// <summary>
    /// The step to announce in chat, at most once per session; null otherwise.
    /// </summary>
    /// <remarks>
    /// Survives <see cref="SessionEnded"/>, so a session shorter than a framework tick still
    /// gets its message. Taking it clears it, so a failed post is not retried every frame.
    /// </remarks>
    public float? TakeAnnouncement()
    {
        var announcement = this.pendingAnnouncement;
        this.pendingAnnouncement = null;
        return announcement;
    }

    // Still too large if the latest step is, as well as for a while after the last one
    // that was. The first half matters for a preview that sends one step and then sits
    // there: no further steps arrive, and the radius is just as wrong as it was.
    private bool RunningWarn(long nowMs)
        => this.sawTooLarge
           && (this.latestStep > WarnAboveWorldUnits
               || nowMs - this.lastTooLargeAt < ClearAfterMilliseconds);
}
