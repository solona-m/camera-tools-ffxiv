using Dalamud.Configuration;

namespace CameraToolsXIV;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Allows the free camera to engage during normal play rather than only in Group Pose
    /// and cutscenes.
    /// </summary>
    /// <remarks>
    /// Off by default and deliberately opt-in. In Group Pose an untethered camera is a
    /// photography tool; in the overworld the same camera sees through walls and past
    /// draw limits, which is a materially different thing to be running.
    /// </remarks>
    public bool AllowOutsideGpose { get; set; }

    /// <summary>
    /// Converts an add-on's step values into FFXIV world units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Should normally stay at 1. The add-on reprojects each frame assuming the camera
    /// moved exactly as far as it asked, so scaling here puts the camera somewhere the
    /// shader does not think it is. Its focus control is a ratio and can absorb a constant
    /// factor, but only within its own range: at a scale of 0.06 the focus delta needed is
    /// some fifteen times what the slider offers, and the subject can never be brought
    /// into focus.
    /// </para>
    /// <para>
    /// Use the add-on's own blur radius to control how far the camera travels. That is the
    /// aperture size, and unlike this it is a value the shader knows about.
    /// </para>
    /// </remarks>
    public float StepScale { get; set; } = 1.0f;

    /// <summary>
    /// Mirrors the direction of add-on steps.
    /// </summary>
    /// <remarks>
    /// An escape hatch, not an expected setting. If the step direction were mirrored the
    /// parallax would invert, and the add-on would focus on the background when asked to
    /// focus on the foreground. The basis we publish checks out as left-handed and
    /// self-consistent, so this should not be needed -- but it is the one assumption that
    /// cannot be verified without running a stack, and flipping it is a decisive test.
    /// </remarks>
    public bool InvertStepDirection { get; set; }
}
