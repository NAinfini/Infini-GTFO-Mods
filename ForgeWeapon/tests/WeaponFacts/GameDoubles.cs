// This project compiles the game-independent half of the package: the three contract files and the module
// definition they register through. None of them names a game type, so this file declares none — and the reference
// to Modules-ASM is kept so that a member one of those files accidentally reaches for fails the build here rather
// than in the game. It exists so the `Compile` list reads like the other focused projects in this package and so a
// future case that does need a double has one place to put it.
namespace ForgeWeapon.Tests.WeaponFacts;
