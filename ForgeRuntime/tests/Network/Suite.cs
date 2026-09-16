using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ForgeRuntime.Network;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// Minimal assertions for the network suite. A failure is recorded with its context instead of thrown, so one run
/// reports every broken expectation rather than only the first.
/// </summary>
internal sealed class Suite
{
    private string _case = "";
    private int _passed;
    private int _failed;
    private readonly List<string> _failures = new();

    internal int Passed => _passed;
    internal int Failed => _failed;
    internal IReadOnlyList<string> Failures => _failures;

    internal void Case(string name) => _case = name;

    internal void True(bool condition, string message)
    {
        if (condition) { _passed++; return; }
        _failed++;
        _failures.Add(_case + ": " + message);
    }

    internal void False(bool condition, string message) => True(!condition, message);

    internal void Equal<T>(T expected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual)) { _passed++; return; }
        _failed++;
        _failures.Add(_case + ": " + message + " (expected " + expected + ", got " + actual + ")");
    }

    internal void Throws<T>(Action action, string expectedCode, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            var code = error is NetworkContractException contract ? contract.Code : "";
            if (expectedCode.Length == 0 || code == expectedCode) { _passed++; return; }
            _failed++;
            _failures.Add(_case + ": " + message + " (threw " + typeof(T).Name + " with code " + code + ")");
            return;
        }
        _failed++;
        _failures.Add(_case + ": " + message + " (nothing was thrown)");
    }

    internal static int SizeOf<T>() where T : struct => Marshal.SizeOf<T>();

    internal int Report()
    {
        foreach (var failure in _failures) Console.WriteLine("FAIL " + failure);
        Console.WriteLine("network suite: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }
}
