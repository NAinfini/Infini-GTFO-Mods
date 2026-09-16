using System.Text.Json;
using ForgeDevelopment.Native;

internal sealed class Suite
{
    private int _passed;
    private int _failed;

    internal void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("ok   " + name);
            return;
        }
        _failed++;
        Console.WriteLine("FAIL: " + name + (detail.Length == 0 ? "" : " -- " + detail));
    }

    internal void Equal<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), "expected <" + expected + "> got <" + actual + ">");

    internal int Report()
    {
        Console.WriteLine();
        Console.WriteLine(_failed + "/" + (_passed + _failed) + " passed");
        return _failed == 0 ? 0 : 1;
    }
}
