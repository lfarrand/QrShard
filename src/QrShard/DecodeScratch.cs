using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Reusable per-worker buffers for the large per-image allocations of decoding.</summary>
internal sealed class DecodeScratch
{
    private Rgb24[]? _pixels;
    private bool[]? _visited;
    private byte[]? _cells;
    private byte[]? _recovered;
    private int[]? _lut;

    public Rgb24[] Pixels(int length)
    {
        if (_pixels is null || _pixels.Length < length)
            _pixels = new Rgb24[length];
        return _pixels;
    }

    public bool[] ClearedVisited(int length)
    {
        if (_visited is null || _visited.Length < length)
            _visited = new bool[length];
        else
            Array.Clear(_visited, 0, length);
        return _visited;
    }

    public byte[] ClearedCells(int length)
    {
        if (_cells is null || _cells.Length < length)
            _cells = new byte[length];
        else
            Array.Clear(_cells, 0, length); // the grid reader ORs bits in
        return _cells;
    }

    public byte[] Recovered(int length)
    {
        if (_recovered is null || _recovered.Length < length)
            _recovered = new byte[length];
        return _recovered;
    }

    public int[] ResetNearestColorLut()
    {
        _lut ??= new int[1 << 15];
        Array.Fill(_lut, -1);
        return _lut;
    }

    private bool[]? _suspects;

    public bool[] ClearedSuspects(int length)
    {
        if (_suspects is null || _suspects.Length < length)
            _suspects = new bool[length];
        else
            Array.Clear(_suspects, 0, length);
        return _suspects;
    }

    private byte[]? _secondChoice;

    public byte[] ClearedSecondChoice(int length)
    {
        if (_secondChoice is null || _secondChoice.Length < length)
            _secondChoice = new byte[length];
        else
            Array.Clear(_secondChoice, 0, length); // bits are ORed in
        return _secondChoice;
    }

    // v2 interleave gather targets — fully overwritten for the requested length, no clearing.
    private byte[]? _gatheredCells;
    private bool[]? _gatheredFlags;
    private byte[]? _gatheredSecond;

    public byte[] GatheredCells(int length) =>
        _gatheredCells is { } b && b.Length >= length ? b : _gatheredCells = new byte[length];

    public bool[] GatheredFlags(int length) =>
        _gatheredFlags is { } b && b.Length >= length ? b : _gatheredFlags = new bool[length];

    public byte[] GatheredSecond(int length) =>
        _gatheredSecond is { } b && b.Length >= length ? b : _gatheredSecond = new byte[length];
}
