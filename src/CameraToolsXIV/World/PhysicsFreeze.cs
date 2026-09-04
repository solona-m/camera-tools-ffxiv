using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace CameraToolsXIV.World;

/// <summary>
/// Stops the game simulating bone physics -- hair, cloth, skirts, tails -- for as long as
/// an add-on is stacking frames.
/// </summary>
/// <remarks>
/// <para>
/// Group Pose freezes what actors <i>do</i>, not what hangs off them. A skirt keeps
/// swinging through a stack, and because it is high-contrast and sits right on the subject
/// it ghosts worse than anything else in frame. Brio ships a button for this; doing it here
/// means one less plugin to run and one less thing to remember to press.
/// </para>
/// <para>
/// The hook is on <c>hkaPartialSkeleton::SetBoneModelTransform</c>, the shared havok write
/// path, and returning early from it drops the simulation's result on the floor. That path
/// is shared by every drawn skeleton, so the freeze is global by construction: the player,
/// other players, NPCs, minions, mounts, and any event object with a skeleton all stop
/// together. There is no per-actor variant of this and none is wanted -- a stack needs the
/// whole frame still, not one character in it.
/// </para>
/// <para>
/// A hook rather than the byte patch Brio and Anamnesis originally used, for a reason
/// specific to this plugin: sessions begin and end on ReShade's render thread. Writing NOPs
/// over live code from a thread that is not the one executing it is a genuine hazard, while
/// enabling a hook from the framework tick is not. <see cref="SetFrozen"/> is therefore
/// game-thread only, and callers on the render thread record intent instead -- the same
/// division the camera override already uses.
/// </para>
/// </remarks>
internal sealed class PhysicsFreeze : IDisposable
{
    /// <summary>
    /// The function prologue of <c>hkaPartialSkeleton::SetBoneModelTransform</c>.
    /// </summary>
    /// <remarks>
    /// From Anamnesis' <c>RemoteController/Interop/HookDelegates.cs</c>. This is the one
    /// piece of the plugin that has to be re-checked when the game is patched; everything
    /// else resolves through FFXIVClientStructs or a vtable.
    /// </remarks>
    private const string SetBoneModelTransformSignature =
        "48 8B C4 48 89 58 18 55 56 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? " +
        "0F 29 70 B8 0F 29 78 A8 44 0F 29 40 ?? 44 0F 29 48 ?? 48 8B 05 ?? ?? ?? ??";

    /// <summary>
    /// A member function, so the first argument is the <c>this</c> pointer. Declared thiscall
    /// by Anamnesis, which on x64 is the same register layout as any other member call --
    /// the same shape <see cref="Camera.CameraController"/> already hooks with.
    /// </summary>
    private delegate nint SetBoneModelTransformDelegate(
        nint partialSkeleton,
        ulong boneId,
        nint transform,
        byte updateSecondaryPose,
        byte propagate);

    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;

    private Hook<SetBoneModelTransformDelegate>? hook;

    // Read by the detour on the game thread and written by SetFrozen on the same thread, so
    // no synchronisation is needed. It exists at all only because disabling a hook is not
    // instantaneous: MinHook has to wait for threads to leave the trampoline, and a call
    // already inside the detour must still see a coherent value.
    private bool frozen;

    public PhysicsFreeze(IGameInteropProvider interop, IPluginLog log)
    {
        this.interop = interop;
        this.log = log;
    }

    /// <summary>Whether the signature resolved and the freeze can be applied.</summary>
    public bool Available { get; private set; }

    /// <summary>Why the freeze is unavailable, in words a user can act on.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Whether physics is currently being suppressed.</summary>
    public bool Frozen => this.frozen;

    /// <summary>
    /// Resolves the signature and installs the hook, left disabled.
    /// </summary>
    /// <remarks>
    /// Separate from the constructor because this scans the game's code section, which is
    /// real work and must not happen on Dalamud's plugin-load thread. It is called from
    /// <see cref="Plugin.Initialize"/>, which already runs on the first framework tick.
    /// </remarks>
    public void Initialize()
    {
        try
        {
            this.hook = this.interop.HookFromSignature<SetBoneModelTransformDelegate>(
                SetBoneModelTransformSignature,
                this.Detour);

            this.Available = true;
            this.log.Information($"Hooked SetBoneModelTransform at 0x{this.hook.Address:X}");
        }
        catch (Exception ex)
        {
            // Fail closed and keep going. A missing signature costs the freeze and nothing
            // else, and a plugin that refused to load over it would be strictly worse.
            this.UnavailableReason =
                "The physics signature did not resolve on this game version.";
            this.log.Warning(ex, "Physics freeze unavailable: signature did not resolve.");
        }
    }

    /// <summary>
    /// Turns the freeze on or off. Game thread only.
    /// </summary>
    /// <remarks>
    /// The hook is enabled and disabled rather than left installed behind a flag.
    /// <c>SetBoneModelTransform</c> is called for every physics bone on every skeleton in
    /// the scene every frame -- easily the hottest code this plugin will ever sit in front
    /// of -- and a stack lasts seconds out of a session that lasts hours. Leaving a detour
    /// on that path the rest of the time would be paying for the feature continuously to
    /// use it occasionally.
    /// </remarks>
    public void SetFrozen(bool value)
    {
        if (this.hook is null || value == this.frozen)
        {
            return;
        }

        // Set before enabling and after disabling, so the flag is never false while the
        // detour is live. The other order lets a bone slip through unfrozen on the first
        // frame, or -- worse on the way out -- be dropped after the freeze is meant to be
        // over.
        if (value)
        {
            this.frozen = true;
            this.hook.Enable();
        }
        else
        {
            this.hook.Disable();
            this.frozen = false;
        }
    }

    /// <summary>
    /// Discards the simulation's result for one bone.
    /// </summary>
    /// <remarks>
    /// Returning the partial skeleton unchanged is what the caller expects on success, so
    /// the physics update carries on believing it wrote. Deliberately does nothing else --
    /// no logging, no try/catch, no allocation. This runs thousands of times a frame, and
    /// anything that costs is multiplied by that.
    /// </remarks>
    private nint Detour(
        nint partialSkeleton,
        ulong boneId,
        nint transform,
        byte updateSecondaryPose,
        byte propagate)
    {
        if (this.frozen && partialSkeleton != nint.Zero)
        {
            return partialSkeleton;
        }

        return this.hook!.Original(partialSkeleton, boneId, transform, updateSecondaryPose, propagate);
    }

    public void Dispose()
    {
        // Dalamud restores the original bytes on disposal, so unlike a hand-rolled code
        // patch there is no window where an unloaded plugin leaves the game patched.
        this.hook?.Disable();
        this.hook?.Dispose();
        this.hook = null;
        this.frozen = false;
    }
}
