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
/// step the camera around a small aperture shape with
/// <see cref="MoveMultishot"/>, wait a few frames for the game to catch up, capture, and
/// finally end the session. Every step is expressed <b>relative to the session's start
/// position</b>, which is why the origin is snapshotted here rather than accumulated.
/// </para>
/// <para>
/// All of these methods run on ReShade's render thread, not the game thread. They only
/// ever record intent on <see cref="CameraController"/>, whose own lock hands the values
/// to the camera update hook. Writing the game's camera struct directly from here would
/// race with the game's update.
/// </para>
/// </remarks>
internal sealed class ScreenshotSession
{
    // ScreenshotSessionStartReturnCode, from IgcsConnector's ConstantsEnums.h.
    private const int AllOk = 0;
    private const int ErrorCameraNotEnabled = 1;
    private const int ErrorAlreadySessionActive = 3;

    // ScreenshotType.
    private const byte TypeHorizontalPanorama = 0;
    private const byte TypeMultiShot = 1;

    private readonly CameraController camera;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private bool active;
    private ViewBasis originBasis;
    private float? originalFovOverride;

    public ScreenshotSession(CameraController camera, IPluginLog log)
    {
        this.camera = camera;
        this.log = log;
    }

    public bool Active
    {
        get { lock (this.gate) { return this.active; } }
    }

    public int Start(byte type)
    {
        lock (this.gate)
        {
            if (this.active)
            {
                return ErrorAlreadySessionActive;
            }

            if (!this.camera.Enabled)
            {
                // The add-on surfaces this to the user as "enable the camera first",
                // which is exactly right: we have nothing to offset from otherwise.
                return ErrorCameraNotEnabled;
            }

            // Freeze the basis for the whole session. Steps are resolved against the
            // orientation at the moment the session began, so that a stack stays
            // rectilinear even if something else nudges the camera mid-session.
            this.originBasis = this.camera.Basis;
            this.originalFovOverride = this.camera.FovOverrideRadians;
            this.camera.SessionOffset = Vector3.Zero;
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

            var offset = (this.originBasis.Right * stepLeftRight) + (this.originBasis.Up * stepUpDown);

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

            // Panoramas rotate about the world up axis so the horizon stays level,
            // rather than about the camera's own up, which would tilt on a pitched shot.
            var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, stepAngle);
            var basis = this.camera.Basis;

            this.camera.SetBasis(new ViewBasis(
                Vector3.Normalize(Vector3.Transform(basis.Right, rotation)),
                Vector3.Normalize(Vector3.Transform(basis.Up, rotation)),
                Vector3.Normalize(Vector3.Transform(basis.Forward, rotation))));
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

            this.camera.SessionOffset = Vector3.Zero;
            this.camera.FovOverrideRadians = this.originalFovOverride;
            this.camera.SetBasis(this.originBasis);
            this.active = false;
        }

        this.log.Information("IGCS session ended.");
    }
}
