using System;
using System.Numerics;

namespace CameraToolsXIV.Camera;

/// <summary>
/// A camera orientation decomposed into world-space basis vectors, plus the quaternion
/// and Euler angles derived from it.
/// </summary>
/// <remarks>
/// This is deliberately built from the game's own view matrix rather than from angles we
/// track ourselves. The IGCS interface wants a specific handedness and axis convention,
/// and guessing at FFXIV's would be the easiest way to ship a subtly wrong orientation
/// that only shows up as misaligned depth-of-field frames. Reading the matrix the game
/// actually rendered with sidesteps the question: whatever convention it uses, the basis
/// vectors we extract are correct in world space by construction.
/// </remarks>
internal readonly struct ViewBasis
{
    public readonly Vector3 Right;
    public readonly Vector3 Up;
    public readonly Vector3 Forward;

    public ViewBasis(Vector3 right, Vector3 up, Vector3 forward)
    {
        Right = right;
        Up = up;
        Forward = forward;
    }

    public static ViewBasis Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>
    /// Extracts the camera basis from a row-major view matrix.
    /// </summary>
    /// <remarks>
    /// A view matrix is the inverse of the camera's world transform, so for a rotation
    /// the inverse is the transpose: the basis vectors are the matrix's <b>columns</b>,
    /// not its rows. Reading them as rows yields a basis that looks plausible and is
    /// wrong for every orientation except the identity.
    /// </remarks>
    public static ViewBasis FromViewMatrix(in Matrix4x4 view)
    {
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var forward = new Vector3(view.M13, view.M23, view.M33);

        return new ViewBasis(Normalize(right), Normalize(up), Normalize(forward));
    }

    /// <summary>
    /// Recovers the camera's world position from a view matrix.
    /// </summary>
    /// <remarks>
    /// The translation row of a view matrix holds the position projected onto the camera
    /// basis and negated, so it has to be projected back out rather than read directly.
    /// Prefer the scene camera's own position field when it is available; this exists as
    /// a cross-check for calibration.
    /// </remarks>
    public Vector3 PositionFromViewMatrix(in Matrix4x4 view)
        => -((Right * view.M41) + (Up * view.M42) + (Forward * view.M43));

    /// <summary>Orientation as a quaternion, in (x, y, z, w) order.</summary>
    public Vector4 ToQuaternion()
    {
        // Rebuild the camera's world-space rotation matrix from the basis (as rows, which
        // is the transpose of the view matrix's rotation part) and convert.
        var rotation = new Matrix4x4(
            Right.X, Right.Y, Right.Z, 0f,
            Up.X, Up.Y, Up.Z, 0f,
            Forward.X, Forward.Y, Forward.Z, 0f,
            0f, 0f, 0f, 1f);

        var q = Quaternion.CreateFromRotationMatrix(rotation);
        return new Vector4(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>Pitch, yaw and roll in radians, matching the IGCS reporting fields.</summary>
    public (float Pitch, float Yaw, float Roll) ToEuler()
    {
        var pitch = MathF.Asin(Math.Clamp(-Forward.Y, -1f, 1f));
        var yaw = MathF.Atan2(Forward.X, Forward.Z);
        var roll = MathF.Atan2(Right.Y, Up.Y);
        return (pitch, yaw, roll);
    }

    private static Vector3 Normalize(Vector3 v)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
}
