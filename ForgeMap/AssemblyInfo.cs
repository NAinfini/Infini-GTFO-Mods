using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ForgeMap.Identity.Tests")]
// The contract files spell a row's binding and its handler once, as internal members, because a second spelling
// outside the contract is exactly what the declaration exists to prevent. The native half publishes through those
// same members, so it reads them here rather than restating an id.
[assembly: InternalsVisibleTo("ForgeMap.Native")]
// The Map native adapter compiles the native half's own sources against game doubles, so it reads the same
// internal contract members ForgeMap.Native reads: a row's binding and its handler are spelled once, in the
// contract that declares them, and a second spelling is what the declaration exists to prevent.
[assembly: InternalsVisibleTo("ForgeMap.MapNativeAdapter.Tests")]
