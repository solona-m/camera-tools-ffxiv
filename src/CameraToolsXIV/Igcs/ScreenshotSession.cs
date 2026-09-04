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
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private bool active;
    private Vector2 lastRawStep;
    private Vector3 lastOffset;

    public ScreenshotSession(CameraController camera, Configuration configuration, IPluginLog log)
    {
        this.camera = camera;
        this.configuration = configuration;
        this.log = log;
    }

    public bool Active
    {
        get { lock (this.gate) { return this.active; } }
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
            // Scaled into world units: the add-on's steps are in camera-tool units, and
            // converting them is the camera tool's job, not the add-on's.
            var scale = this.configuration.StepScale;
            var basis = this.camera.HoldBasis;
            var offset = (basis.Right * stepLeftRight * scale) + (basis.Up * stepUpDown * scale);

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
    /// Used on unload and when the camera stops being permitted. An add-on that crashes
    /// or is disabled mid-stack never sends <c>IGCS_EndScreenshotSession</c>, and without
    /// this the camera would stay frozen with no visible way to release it.
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
        }

        this.log.Warning("IGCS session aborted.");
    }
}
