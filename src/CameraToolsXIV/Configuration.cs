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
    /// IGCS add-ons send steps in camera-tool units, not world units -- IgcsConnector's
    /// own interface notes that steps are "not divided by movementspeed yet, so it has to
    /// be done locally". Each camera tool applies its game's own scale. FFXIV's units are
    /// roughly metres, so an aperture wants centimetres of travel; applying the add-on's
    /// values raw throws the camera metres per step, through whatever is nearby.
    /// </remarks>
    public float StepScale { get; set; } = 0.02f;
}
