using System;
using System.Numerics;
using CameraToolsXIV.Camera;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// Implements the IGCS screenshot-session semantics that accumulation depth-of-field
/// drives the camera through.
/// </summary>
/// <remarks>
/// <para>
/// Parallax DoF and IGCS DoF both follow the same sequence: start a multishot session,
/// step the camera around a small aperture shape with <see cref="MoveMultishot"/>, wait a
/// few frames for the game to catch up, capture, and finally end the session. Every step
/// is expressed <b>relative to the session's start position</b>, which is why the origin
/// is frozen at the start rather than accumulated.
/// </para>
/// <para>
/// The session is also the only thing that takes the camera over. Outside one the user
/// keeps whatever they framed the shot with, so this plugin does not compete with GPose's
/// own controls or with Cammy and Brio.
/// </para>
/// <para>
/// All of these methods run on ReShade's render thread, not the game thread. They only
/// record intent on <see cref="CameraController"/>, whose lock hands the values to the
/// camera update hook. Writing the game's camera struct directly from here would race
/// with the game's own update.
/// </para>
/// </remarks>
internal sealed class ScreenshotSession
{
    // ScreenshotSessionStartReturnCode, from IgcsConnector's ConstantsEnums.h.
    private const int AllOk = 0;
    private const int ErrorCameraNotEnabled = 1;
    private const int ErrorAlreadySessionActive = 3;
    private const int ErrorUnknownError = 5;

    private readonly CameraController camera;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private bool active;
    private bool wasAborted;
    private byte sessionType;
    private Vector2 lastRawStep;
    private Vector3 lastOffset;

    // Panorama used to record nothing, which made a sweeping camera indistinguishable from
    // a still one on the panel: multishot showed its step, panorama showed the zeros it had
    // never overwritten. Anything that can move the camera has to be measurable, or the
    // readout quietly lies about which call is driving it.
    private float lastPanoramaAngle;
    private float totalPanoramaAngle;
    private int panoramaCalls;

    public ScreenshotSession(CameraController camera, IPluginLog log)
    {
        this.camera = camera;
        this.log = log;
    }

    public bool Active
    {
        get { lock (this.gate) { return this.active; } }
    }

    /// <summary>
    /// Whether the last session ended by being cut short rather than by the add-on.
    /// </summary>
    /// <remarks>
    /// Worth surfacing, because there is no way to tell the add-on. It goes on believing
    /// it holds the camera, and the user sees a ghosted preview that looks like a focus
    /// problem rather than an abandoned session.
    /// </remarks>
    public bool WasAborted
    {
        get { lock (this.gate) { return this.wasAborted; } }
    }

    /// <summary>The most recent step as the add-on sent it, before scaling.</summary>
    public Vector2 LastRawStep
    {
        get { lock (this.gate) { return this.lastRawStep; } }
    }

    /// <summary>The world-space offset that step became, for calibrating the scale.</summary>
    public Vector3 LastOffset
    {
        get { lock (this.gate) { return this.lastOffset; } }
    }

    /// <summary>The session type the add-on asked for: 0 panorama, 1 multishot.</summary>
    /// <remarks>
    /// Recorded because the plugin does not branch on it -- both session types take the
    /// camera the same way, and the add-on then drives whichever move call it likes. That
    /// makes the type the only evidence of what the add-on thinks it is doing.
    /// </remarks>
    public byte SessionType
    {
        get { lock (this.gate) { return this.sessionType; } }
    }

    /// <summary>The most recent panorama step as the add-on sent it.</summary>
    public float LastPanoramaAngle
    {
        get { lock (this.gate) { return this.lastPanoramaAngle; } }
    }

    /// <summary>Every panorama step so far, summed, and how many there were.</summary>
    public (float Total, int Calls) PanoramaTotal
    {
        get { lock (this.gate) { return (this.totalPanoramaAngle, this.panoramaCalls); } }
    }

    public int Start(byte type)
    {
        lock (this.gate)
        {
            if (this.active)
            {
                return ErrorAlreadySessionActive;
            }

            if (!this.camera.Armed)
            {
                // The add-on surfaces this to the user as "enable the camera first",
                // which is exactly right: we have nothing to offset from otherwise.
                return ErrorCameraNotEnabled;
            }

            if (!this.camera.BeginHold())
            {
                return ErrorUnknownError;
            }

            this.active = true;
            this.wasAborted = false;
            this.sessionType = type;

            // Cleared per session, so the panel describes this stack rather than the last
            // one that happened to touch each field.
            this.lastRawStep = Vector2.Zero;
            this.lastOffset = Vector3.Zero;
            this.lastPanoramaAngle = 0f;
            this.totalPanoramaAngle = 0f;
            this.panoramaCalls = 0;
        }

        this.log.Information($"IGCS session started (type {type}).");
        return AllOk;
    }

    /// <summary>
    /// Steps the camera within the session. This is the call accumulation depth-of-field
    /// makes for every frame of a stack.
    /// </summary>
    /// <param name="stepLeftRight">Positive moves right, negative left.</param>
    /// <param name="stepUpDown">Positive moves up, negative down.</param>
    /// <param name="fovDegrees">Session FoV in degrees; values &lt;= 0 leave it alone.</param>
    /// <param name="fromStartPosition">
    /// When true the offset replaces the current one rather than adding to it. Depth of
    /// field always passes true, which is what keeps a long stack free of drift.
    /// </param>
    public void MoveMultishot(float stepLeftRight, float stepUpDown, float fovDegrees, bool fromStartPosition)
    {
        lock (this.gate)
        {
            if (!this.active)
            {
                return;
            }

            // Resolved against the basis frozen at the start of the hold, so a long stack
            // stays rectilinear even if something else nudges the camera mid-session.
            //
            // Taken at face value, one add-on unit to one world unit. There was a scale
            // factor here and a switch to mirror the horizontal axis; both are gone. The
            // shader reprojects each frame assuming the camera moved exactly as far as it
            // asked, so any factor but one puts the camera where the shader does not think
            // it is, and the aperture size is the add-on's blur radius to set, not ours to
            // rescale. The mirror was a diagnostic for a handedness question that the
            // published basis has since answered.
            var basis = this.camera.HoldBasis;
            var offset = (basis.Right * stepLeftRight) + (basis.Up * stepUpDown);

            this.lastRawStep = new Vector2(stepLeftRight, stepUpDown);
            this.lastOffset = offset;

            this.camera.SessionOffset = fromStartPosition
                ? offset
                : this.camera.SessionOffset + offset;

            if (fovDegrees > 0f)
            {
                this.camera.FovOverrideRadians = float.DegreesToRadians(fovDegrees);
            }
        }
    }

    /// <summary>Rotates the camera for a panorama session.</summary>
    /// <param name="stepAngle">Angle in radians; positive rotates right.</param>
    public void MovePanorama(float stepAngle)
    {
        lock (this.gate)
        {
            if (!this.active)
            {
                return;
            }

            this.lastPanoramaAngle = stepAngle;
            this.totalPanoramaAngle += stepAngle;
            this.panoramaCalls++;

            this.camera.RotateHold(stepAngle);
        }
    }

    public void End()
    {
        lock (this.gate)
        {
            if (!this.active)
            {
                return;
            }

            this.camera.ReleaseHold();
            this.active = false;
        }

        this.log.Information("IGCS session ended.");
    }

    /// <summary>
    /// Abandons a session without waiting for the add-on to end it.
    /// </summary>
    /// <remarks>
    /// Reserved for unload and for an explicit request by the user. An add-on that
    /// crashes or is disabled mid-stack never sends <c>IGCS_EndScreenshotSession</c>, so
    /// without an escape the camera would stay frozen with no way to release it.
    /// <para>
    /// Deliberately not called for transient game-state changes. The add-on cannot be
    /// told, so it keeps compositing against a camera that has stopped moving, and the
    /// ghosted result looks like a focus problem rather than an abandoned session.
    /// </para>
    /// </remarks>
    public void Abort()
    {
        lock (this.gate)
        {
            if (!this.active)
            {
                return;
            }

            this.camera.ReleaseHold();
            this.active = false;
            this.wasAborted = true;
        }

        this.log.Warning("IGCS session aborted.");
    }
}
