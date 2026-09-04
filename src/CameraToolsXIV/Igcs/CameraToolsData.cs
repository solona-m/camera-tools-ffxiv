using System.Numerics;
using System.Runtime.InteropServices;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// The payload a camera tool publishes into the add-on's shared buffer, laid out to
/// match <c>CameraToolsData</c> in IgcsConnector's <c>CameraToolsData.h</c>.
/// </summary>
/// <remarks>
/// Every field's units are fixed by that interface and are easy to get subtly wrong:
/// <see cref="Fov"/> is in <b>degrees</b> while the game stores radians, and
/// <see cref="Pitch"/>/<see cref="Yaw"/>/<see cref="Roll"/> are in <b>radians</b>.
/// The struct is 84 bytes; every member is 4-byte aligned, so sequential layout
/// introduces no padding.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CameraToolsData
{
    /// <summary>
    /// Size of the C++ struct this mirrors: 4 flag bytes, a float, then 15 more floats.
    /// </summary>
    /// <remarks>
    /// Checked at runtime rather than assumed. <see cref="System.Numerics.Vector4"/> is a
    /// JIT intrinsic with 16-byte natural alignment, so the packing that makes this come
    /// out at 84 bytes is a guarantee worth verifying instead of relying on.
    /// </remarks>
    public const int ExpectedSizeBytes = 84;

    /// <summary>1 when the user has our free camera enabled, 0 otherwise.</summary>
    public byte CameraEnabled;

    /// <summary>1 while an add-on owns the camera and user input must not move it.</summary>
    public byte CameraMovementLocked;

    public byte Reserved1;
    public byte Reserved2;

    /// <summary>Vertical field of view, in DEGREES.</summary>
    public float Fov;

    public Vector3 Coordinates;

    /// <summary>Orientation as (x, y, z, w).</summary>
    public Vector4 LookQuaternion;

    public Vector3 Up;
    public Vector3 Right;
    public Vector3 Forward;

    /// <summary>Euler angles in RADIANS.</summary>
    public float Pitch;

    public float Yaw;
    public float Roll;
}
