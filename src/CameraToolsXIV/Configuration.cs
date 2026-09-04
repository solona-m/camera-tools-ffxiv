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

    // A step scale and a horizontal mirror used to live here. Both are gone: steps are
    // taken at face value now, because the shader reprojects assuming the camera moved
    // exactly as far as it asked, and the aperture size belongs to the add-on's blur
    // radius. Old configs keep the stale keys on disk harmlessly -- unknown properties are
    // ignored on load -- so there is nothing to migrate.

    /// <summary>
    /// Publishes horizontal field of view rather than the vertical one the game stores.
    /// </summary>
    /// <remarks>
    /// Marty's Parallax DoF declares its FoV uniform as "hor FOV, rad" and scales its
    /// reprojection by <c>tan(FOV * 0.5)</c>, so a vertical angle understates that scale by
    /// the aspect ratio -- at 16:9, 44.7 degrees where roughly 72.4 is wanted. The IGCS
    /// interface itself only says "degrees" without settling which, so this stays
    /// switchable in case another add-on reads it the other way.
    /// </remarks>
    public bool PublishHorizontalFov { get; set; } = true;

    /// <summary>
    /// Stops hair, cloth and other bone physics for the duration of a stack.
    /// </summary>
    /// <remarks>
    /// On by default, and the reason is the same one that makes the camera override
    /// session-scoped: an accumulation stack needs every frame in it to match, and a skirt
    /// swinging through one ghosts harder than anything else in shot. Group Pose does not
    /// freeze this on its own.
    /// <para>
    /// The freeze lands wherever the simulation happens to be when the stack begins, which
    /// is right for the stack and arbitrary as a pose. If a particular drape matters, hold
    /// still until it settles before starting the stack.
    /// </para>
    /// </remarks>
    public bool FreezePhysicsDuringSession { get; set; } = true;

    /// <summary>
    /// Stops the game's logic clock for the duration of a stack, holding the environment
    /// still along with the actors.
    /// </summary>
    /// <remarks>
    /// On by default. Freezing physics leaves water, foliage, weather and particles moving,
    /// and every one of those ghosts across a stack that takes four minutes to render. The
    /// only lever that reaches them is the frame delta itself, so this is a blunt
    /// instrument: it stops chat, movement, and everything else the game ticks, for as long
    /// as the stack runs.
    /// <para>
    /// That is a real cost, and it is the default anyway. A stack is a deliberate act that
    /// occupies the game for minutes whatever happens; a still world is the whole point of
    /// running one, and discovering afterwards that the foliage moved is worse than knowing
    /// in advance that chat will not arrive until it finishes.
    /// </para>
    /// </remarks>
    public bool PauseWorldDuringSession { get; set; } = true;
}
