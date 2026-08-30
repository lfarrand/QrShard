namespace QrShard.Display;

/// <summary>Advances flipbook playback one tick, skipping only when trailing the clock.</summary>
internal static class FlipbookIndexer
{
    public const long CatchUpFrames = 10;

    public static long Advance(long timeIndex, long lastRawIndex)
    {
        long rawIndex = Math.Min(timeIndex, lastRawIndex + 1);
        if (timeIndex - rawIndex > CatchUpFrames)
            rawIndex = timeIndex;
        return rawIndex < 0 ? 0 : rawIndex;
    }

    public static bool OncePastLast(long rawIndex, int count) => rawIndex >= count;
}
