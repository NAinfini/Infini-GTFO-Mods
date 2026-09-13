using System.Text.RegularExpressions;
using Mono.Cecil;

internal sealed class NativeBodyMap
{
    private readonly Dictionary<string, List<string>> _methods = new();
    private readonly Dictionary<string, int> _owners = new();

    internal NativeBodyMap(string path)
    {
        string space = "", type = "", address = "";
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("// Namespace:")) space = line[13..].Trim();
            var declaration = Regex.Match(line, @"\b(?:class|struct) (\S+)");
            if (declaration.Success) type = (space.Length == 0 ? "" : space + ".") + declaration.Groups[1].Value;
            var rva = Regex.Match(line, @"// RVA: (0x[0-9A-Fa-f]+)");
            if (rva.Success) { address = rva.Groups[1].Value; continue; }
            if (address.Length == 0 || line.TrimStart().StartsWith("//")) continue;
            var method = Regex.Match(line, @"([\w.]+)\(");
            if (!method.Success) continue;
            var key = type + "/" + method.Groups[1].Value;
            if (!_methods.TryGetValue(key, out var addresses)) _methods[key] = addresses = new();
            addresses.Add(address);
            _owners.TryGetValue(address, out var count); _owners[address] = count + 1;
            address = "";
        }
    }

    internal void Check(MethodDefinition method)
    {
        var owner = method.DeclaringType;
        var typeName = owner.FullName.Replace('/', '.');
        if (owner.IsNested)
        {
            while (owner.IsNested) owner = owner.DeclaringType;
            if (owner.Namespace.Length > 0) typeName = typeName[(owner.Namespace.Length + 1)..];
        }
        var key = typeName + "/" + method.Name;
        if (!_methods.TryGetValue(key, out var addresses)) throw new Exception("Missing native body evidence: " + key);
        foreach (var address in addresses)
            if (_owners[address] != 1)
                throw new Exception($"Unsafe shared native body: {key} at {address}, {_owners[address]} method entries. A valid managed signature does not make this hook safe.");
    }
}
