using System.Text.Json;
using ForgeRuntime.Framework;

/// <summary>The suite's own bookkeeping: one row per case, one assertion helper, and the result-row readers a
/// case uses to hold a handler to the row the catalog declares. A case that throws is a failed row, and the
/// master flag is restored after every case so one case's client-side gate cannot leak into the next.</summary>
internal static class Probe
{
    internal sealed record CheckRow(string Id, bool Passed, string Detail);
    internal static readonly List<CheckRow> Rows = new();

    internal static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }

    internal static void Case(string id, Action test)
    {
        try { test(); Rows.Add(new(id, true, "passed")); }
        catch (Exception error)
        {
            var detail = error is RuntimeContractException contract
                ? contract.GetType().Name + "/" + contract.Code + ": " + contract.Message + "\n" + error.StackTrace
                : error.ToString();
            Rows.Add(new(id, false, detail));
            Console.Error.WriteLine("FAIL " + id + ": " + detail);
        }
        finally { SNetwork.SNet.IsMaster = true; }
    }

    internal static JsonElement Row(CommandResult result, int index)
        => result.Outputs.GetProperty("results").EnumerateArray().ElementAt(index);

    internal static string Code(CommandResult result, int index) => Row(result, index).GetProperty("code").GetString()!;
}
