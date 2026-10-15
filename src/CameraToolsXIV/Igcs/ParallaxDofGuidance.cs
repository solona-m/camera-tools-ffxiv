using System.Collections.Generic;
using System.Globalization;

namespace CameraToolsXIV.Igcs;

/// <summary>
/// The Parallax DoF settings that work in FFXIV, and what the plugin says when a stack is
/// using the wrong aperture.
/// </summary>
/// <remarks>
/// The plugin can only tell the user these. Parallax DoF keeps its settings to itself --
/// nothing in ReShade.ini or the preset -- so there is nothing to write them to.
/// <para>
/// Mirrors the table under "Suggested Parallax DoF settings" in the README; change both
/// together. Kept free of Dalamud so the harness can check the values and wording.
/// </para>
/// </remarks>
internal static class ParallaxDofGuidance
{
    public const string RecommendedBlurRadius = "0.003";

    /// <summary>The row label of the one setting that ruins a stack when left at default.</summary>
    public const string BlurRadiusSetting = "Blur Radius";

    public static IReadOnlyList<(string Setting, string Value)> Recommended { get; } =
    [
        ("Accumulation Init / Delay", "4 frames each"),
        ("Rangefinder Focus", "~15.1 (per shot)"),
        (BlurRadiusSetting, RecommendedBlurRadius),
        ("Bokeh Intensity / Gamma / Colour", "0.550 / 0.580 / 0.370"),
        ("Aperture", "Circular, aspect 1.000"),
        ("Sample Count", "1024 (about four minutes)"),
    ];

    public static string WarningHeading(bool fromEndedSession)
        => fromEndedSession
            ? "Last stack's Blur Radius was far too large for FFXIV"
            : "Blur Radius is far too large for FFXIV";

    // Invariant throughout: the user types this number into Parallax DoF, and "0,003" on
    // a German locale is not what its slider takes.
    public static string WarningBody(float step)
        => string.Format(
            CultureInfo.InvariantCulture,
            "The add-on moved the camera {0:F2} world units (about {1:F0} cm) in one step. " +
            "FFXIV's unit is about a metre, so that aperture sees behind your subject and " +
            "blurs it along with the background. Set Blur Radius to {2} in Parallax DoF's " +
            "own window -- this plugin cannot change it for you. The other values are " +
            "under \"Recommended Parallax DoF settings\" below. Using IgcsConnector and " +
            "meant to move this far? Ignore this.",
            step,
            step * 100f,
            RecommendedBlurRadius);

    public static string ChatMessage(float step)
        => string.Format(
            CultureInfo.InvariantCulture,
            "[Camera Tools] Parallax DoF is moving the camera {0:F2} world units per step, which " +
            "blurs your subject too. Set its Blur Radius to {1}; /camtools has the rest of the " +
            "settings. (IgcsConnector users moving this far on purpose can ignore this.)",
            step,
            RecommendedBlurRadius);
}
