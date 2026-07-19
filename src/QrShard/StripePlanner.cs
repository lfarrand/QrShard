namespace QrShard;

/// <summary>Chooses cross-shard stripe geometry for a recovery target.</summary>
internal interface IStripePlanner
{
    (int StripeData, int StripeParity) PlanStripes(int count, int recoveryPercent);
}

/// <summary>
/// Chooses stripe geometry for a given recovery target. Parity is a percentage of the data
/// images per stripe; the stripe size is capped so data + parity fits one GF(2^8) block (255).
/// </summary>
internal sealed class StripePlanner : IStripePlanner
{
    public (int StripeData, int StripeParity) PlanStripes(int count, int recoveryPercent)
    {
        if (recoveryPercent <= 0 || count < 1)
            return (0, 0);

        // Largest stripe whose data+parity still fits 255: S * (1 + r/100) <= 255.
        int maxData = (int)Math.Floor(CrossShardFec.MaxShardsPerStripe / (1.0 + recoveryPercent / 100.0));
        int stripeData = Math.Clamp(Math.Min(count, maxData), 1, CrossShardFec.MaxShardsPerStripe - 1);
        int stripeParity = Math.Max(1, (int)Math.Ceiling(stripeData * recoveryPercent / 100.0));
        while (stripeData + stripeParity > CrossShardFec.MaxShardsPerStripe && stripeData > 1)
            stripeData--;
        return (stripeData, stripeParity);
    }
}
