namespace QrShard.Recorder;

/// <summary>Continues <c>yyyyMMdd-N</c> numbering when the folder already has captures for the date.</summary>
internal static class CaptureSequence
{
    public static int Next(string folder, string date, string extension)
    {
        int max = 0;
        foreach (string file in Directory.EnumerateFiles(folder, $"{date}-*.{extension}"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(stem.AsSpan(date.Length + 1), out int n) && n > max)
                max = n;
        }
        return max + 1;
    }
}
