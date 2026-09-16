// The focused suite for `C-glue-foam`: `forge.action.combat.foaming`.
//
// It registers the provider's own declaration with that one row merged in — the insertion the integration patch
// performs — and dispatches the handler the module registered. Nothing here touches the shared registry, the plugin
// lifetime or a Harmony patch: the suite compiles the production sources it exercises and answers with the checks
// below.
using static T;

GlueCases.Run();

int failed = Rows.Count(row => !row.Passed);
foreach (var row in Rows) Console.WriteLine((row.Passed ? "PASS " : "FAIL ") + row.Id + (row.Passed ? "" : " — " + row.Detail));
Console.WriteLine($"{Rows.Count - failed}/{Rows.Count} passed");
return failed == 0 ? 0 : 1;
