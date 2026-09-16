using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>The one route a fact takes out of this package: the registration that owns the `gtfo.equipment`
/// namespace. It is a delegate rather than a session lookup because the observation is a plain object over a
/// kernel — a focused test binds the same shape to its own registration handle, and the game binds it to the
/// identity session, so nothing in the observation reaches for a global to publish.</summary>
internal delegate DispatchResult FactRouter(RuntimeEvent value);
