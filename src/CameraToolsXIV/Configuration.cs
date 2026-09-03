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
}
