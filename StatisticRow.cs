namespace InfiniTweaks;

internal sealed class StatisticRow
{
    internal long Fired, Hit;
    internal float Damage;
    internal byte AccuracySource;
    internal uint AccuracySequence, DamageSequence;

    internal void ClearAccuracy()
    {
        Fired = Hit = 0;
        AccuracySource = 0; AccuracySequence = 0;
    }

    internal void ClearHostData()
    {
        Damage = 0; DamageSequence = 0;
        if (AccuracySource == 2) ClearAccuracy();
    }

    internal bool Apply(byte kind, uint sequence, long fired, long hit, float damage)
    {
        if (kind is < 1 or > 3 || sequence == 0 || fired < 0 || hit < 0 || hit > fired || !float.IsFinite(damage) || damage < 0) return false;
        if (kind == 3)
        {
            if (sequence <= DamageSequence) return false;
            DamageSequence = sequence; Damage = damage;
        }
        else
        {
            if (kind == 2 && AccuracySource == 1) return false;
            if (AccuracySource == kind && sequence <= AccuracySequence) return false;
            AccuracySequence = sequence; AccuracySource = kind;
            Fired = fired; Hit = hit;
        }
        return true;
    }
}
