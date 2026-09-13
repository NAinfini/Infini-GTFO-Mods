namespace InfiniTweaks;

// Historical GTFO template data; source:
// https://github.com/Mamizu1028/GTFO_BoosterTweaker/blob/main/Hikaria.BoosterTweaker/Resources/OldBoosterTemplates.json
// Existing persistent inventory may retain these templates after a rundown update.
internal static class HistoricalBoosterTemplates
{
    internal sealed record Template(uint Id, int Category, BoosterRollRules.Roll[] Effects, BoosterRollRules.Roll[][] Slots);
    internal static readonly Template[] All =
    {
        new(1, 0, new BoosterRollRules.Roll[] { new(8, 1.13f) }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(7, 1.15f), new(50, 1.15f) } }),
        new(22, 0, new BoosterRollRules.Roll[] { new(12, 1.25f) }, new BoosterRollRules.Roll[][] {  }),
        new(18, 0, new BoosterRollRules.Roll[] { new(6, 1.25f) }, new BoosterRollRules.Roll[][] {  }),
        new(23, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(10, 1.1f), new(11, 1.1f) } }),
        new(24, 0, new BoosterRollRules.Roll[] { new(10, 1.1f), new(11, 1.1f) }, new BoosterRollRules.Roll[][] {  }),
        new(4, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(54, 1.1f) } }),
        new(25, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(52, 1.1f), new(53, 1.1f) } }),
        new(7, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(27, 1.25f), new(28, 1.15f), new(29, 1.1f), new(31, 1.15f), new(32, 1.3f) } }),
        new(20, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(40, 1.3f), new(39, 1.3f), new(33, 1.1f) } }),
        new(10, 0, new BoosterRollRules.Roll[] { new(34, 1.4f), new(35, 1.1f) }, new BoosterRollRules.Roll[][] {  }),
        new(21, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(41, 1.18f) } }),
        new(13, 0, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(36, 1.2f), new(37, 1.2f), new(38, 1.2f), new(5, 1.25f) } }),
        new(26, 1, new BoosterRollRules.Roll[] { new(7, 1.3f), new(8, 1.3f), new(50, 1.3f) }, new BoosterRollRules.Roll[][] {  }),
        new(27, 1, new BoosterRollRules.Roll[] { new(12, 1.6f) }, new BoosterRollRules.Roll[][] {  }),
        new(28, 1, new BoosterRollRules.Roll[] { new(6, 2f) }, new BoosterRollRules.Roll[][] {  }),
        new(29, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(10, 1.2f), new(11, 1.2f) } }),
        new(30, 1, new BoosterRollRules.Roll[] { new(10, 1.2f), new(11, 1.2f) }, new BoosterRollRules.Roll[][] {  }),
        new(31, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(54, 1.3f) } }),
        new(32, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(52, 1.2f), new(53, 1.2f) } }),
        new(33, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(27, 1.6f), new(28, 1.25f), new(29, 1.2f), new(31, 1.3f), new(32, 1.55f) }, new BoosterRollRules.Roll[] { new(39, 1.3f), new(33, 1.18f), new(40, 1.4f) } }),
        new(35, 1, new BoosterRollRules.Roll[] { new(34, 1.8f), new(35, 1.18f) }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(49, 0.95f) } }),
        new(36, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(41, 1.18f) } }),
        new(37, 1, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(36, 1.35f), new(37, 1.35f), new(38, 1.35f), new(5, 1.6f) }, new BoosterRollRules.Roll[] { new(6, 0.87f), new(34, 0.83f) } }),
        new(38, 2, new BoosterRollRules.Roll[] { new(7, 1.5f), new(8, 2f), new(50, 1.5f) }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(54, 0.95f), new(12, 0.87f), new(11, 0.87f) } }),
        new(39, 2, new BoosterRollRules.Roll[] { new(12, 2f) }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(54, 0.95f) } }),
        new(40, 2, new BoosterRollRules.Roll[] { new(6, 2.5f) }, new BoosterRollRules.Roll[][] {  }),
        new(41, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(10, 1.4f), new(11, 1.4f) } }),
        new(42, 2, new BoosterRollRules.Roll[] { new(10, 1.4f), new(11, 1.4f) }, new BoosterRollRules.Roll[][] {  }),
        new(43, 2, new BoosterRollRules.Roll[] { new(49, 1.6f) }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(11, 0.9f) } }),
        new(44, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(52, 1.3f), new(53, 1.3f) } }),
        new(45, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(27, 2f), new(28, 1.4f), new(29, 1.3f), new(31, 1.4f), new(32, 2f) }, new BoosterRollRules.Roll[] { new(40, 1.4f), new(39, 1.4f), new(33, 1.3f) }, new BoosterRollRules.Roll[] { new(49, 0.83f), new(11, 0.87f), new(12, 0.71f) } }),
        new(47, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(34, 2.2f), new(35, 1.3f) }, new BoosterRollRules.Roll[] { new(28, 1.4f), new(33, 1.3f), new(50, 1.5f) }, new BoosterRollRules.Roll[] { new(49, 0.83f), new(11, 0.87f) } }),
        new(48, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(41, 1.25f) } }),
        new(49, 2, new BoosterRollRules.Roll[] {  }, new BoosterRollRules.Roll[][] { new BoosterRollRules.Roll[] { new(36, 1.53f), new(37, 1.53f), new(38, 1.53f), new(5, 2f) }, new BoosterRollRules.Roll[] { new(11, 0.87f), new(12, 0.71f), new(34, 0.62f) } }),
    };
}
