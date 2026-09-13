namespace InfiniTweaks;

internal static class MarkerIconCatalog
{
    internal static string Select(MarkerCategory category, uint itemId, string terminalKey)
    {
        var resource = category switch
        {
            MarkerCategory.Health => "health", MarkerCategory.Ammo => "ammo",
            MarkerCategory.Tool => "tool", MarkerCategory.Disinfection => "disinfection",
            MarkerCategory.HSU => "hsu", MarkerCategory.HSUActivator => "hsu-activator",
            MarkerCategory.Terminal => "terminal", MarkerCategory.Generator => "generator",
            MarkerCategory.DisinfectionStation => "disinfection-station",
            MarkerCategory.BulkheadController => "bulkhead-control", _ => null
        };
        if (resource != null) return resource;
        var item = itemId switch
        {
            27 => "keycard-red", 85 => "keycard-blue", 86 => "keycard-green", 87 => "keycard-yellow",
            88 => "keycard-white", 89 => "keycard-black", 90 => "keycard-grey", 91 => "keycard-orange",
            92 => "keycard-purple", 93 => "keycard-gold", 94 => "keycard-brown",
            117 => "fog", 116 => "lock", 114 or 136 => "glow", 130 => "glow-red", 167 => "glow-orange",
            174 => "glow-yellow", 115 => "foam", 139 => "mine", 144 => "foam-mine",
            140 => "syringe-health", 142 => "syringe-speed", 30 => "flashlight",
            131 => "cell", 133 => "turbine", 138 or 154 or 155 => "cargo", 146 => "bulkhead-key", 148 => "cryo",
            137 or 141 => "neonate", 143 or 170 or 175 or 177 => "neonate-open",
            145 => "imprinted-hsu", 151 or 181 => "data-sphere", 164 or 166 => "projector",
            128 => "personnel-id", 129 => "decoder", 147 or 180 or 183 => "hard-drive",
            149 => "glp", 169 => "glp-mk2", 150 => "osip", 153 => "plant-sample",
            165 or 168 or 178 => "data-cube", 179 => "data-cubes", 171 or 172 => "memory-stick",
            176 => "cargo-case", 173 => "collection-case", 113 => "flare",
            _ => null
        };
        if (item != null) return item;
        // Terminal aliases identify custom-rundown carry objects without guessing from display names.
        if (category != MarkerCategory.CarryItem) return "unknown-item";
        return terminalKey.Split('_')[0].ToUpperInvariant() switch
        {
            "CEL" or "CELL" => "cell", "CRYO" => "cryo", "CARGO" => "cargo", _ => "unknown-item"
        };
    }

}
