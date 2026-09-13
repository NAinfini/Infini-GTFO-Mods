using ForgeRuntime.Framework;

namespace ForgeTrigger.Pure;

// A value-type stream local to one evaluation, never shared or persisted.
internal struct SeededStream
{
    private uint state;
    internal SeededStream(long seed)
    {
        if (seed < 0 || seed > uint.MaxValue)
            throw new RuntimeContractException("pure-seed", "Seed must be an explicit unsigned 32-bit integer.");
        state = (uint)seed;
    }

    internal double NextUnit()
    {
        unchecked
        {
            state += 0x6d2b79f5u;
            var value = (state ^ (state >> 15)) * (1u | state);
            value ^= value + ((value ^ (value >> 7)) * (61u | value));
            return (value ^ (value >> 14)) / 4294967296d;
        }
    }
}
