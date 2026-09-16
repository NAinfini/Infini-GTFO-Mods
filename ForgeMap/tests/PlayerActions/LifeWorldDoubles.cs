using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The part of the life-world contract this action slice's fixture relies on. The full contract and its
/// readback value are declared by the player-life trigger half in `Native/PlayerLifeFacts.cs`, a file that also
/// installs the native hooks it carries; an action slice's fixture cannot compile that file without pulling
/// Harmony and every hook target into its own test, so it declares the members it needs here and nothing else.
///
/// `PlayerIdentityModule` answers both of them, which is the check this declaration performs: the identity half
/// is the one path every recipient of these actions is resolved through, so a member it stops answering fails
/// this project's build instead of failing at runtime.
///
/// Integration note: the trigger half's declaration is the authority. Both halves are compiled together into the
/// native project, where the identity half is checked against the real interface; this file is a fixture view,
/// not a second contract.</summary>
internal interface IPlayerLifeWorld
{
    /// <summary>Whether this process may read a recorded life right now: the registration is live, the runtime is
    /// ready and this peer is the authority.</summary>
    bool Authoritative { get; }

    /// <summary>Drops this half's per-world tables. The kernel's world epoch is what invalidates the references
    /// already handed out.</summary>
    void BeginWorld();
}

/// <summary>One read of one player life right now, as the native side reports it: a value the identity half
/// answers and no caller keeps across a native call.</summary>
internal readonly record struct RuntimePlayerLife(bool Alive, bool Downed, bool Revived);
