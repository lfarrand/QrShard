using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QrShard.Benchmarks;

/// <summary>
/// Collects the full hardware/software spec of the machine a benchmark run executed on, so the
/// report's numbers carry their context. Every probe is best-effort: a failed WMI query or a
/// non-Windows host degrades to fewer rows, never to an exception.
/// </summary>
internal static class MachineSpec
{
    public static IReadOnlyList<(string Label, string Value)> Collect()
    {
        var rows = new List<(string, string)>();
        if (OperatingSystem.IsWindows())
        {
            Try(rows, "CPU", Cpu);
            Try(rows, "Motherboard", Motherboard);
            Try(rows, "RAM", Ram);
            Try(rows, "Storage", Storage);
            Try(rows, "OS", WindowsVersion);
        }
        else
        {
            rows.Add(("OS", RuntimeInformation.OSDescription));
        }
        rows.Add((".NET", $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}, runtime {Environment.Version})"));
        rows.Add(("Cores", $"{Environment.ProcessorCount} logical"));
        return rows;
    }

    private static void Try(List<(string, string)> rows, string label, Func<string?> probe)
    {
        try
        {
            string? value = probe();
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add((label, value));
        }
        catch
        {
            // best effort — omit the row rather than fail the report
        }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
            yield return item;
    }

    private static string? Str(ManagementBaseObject o, string property)
    {
        string? value = o.Properties[property]?.Value?.ToString();
        if (value is null)
            return null;
        // SPD/SMBIOS strings routinely carry embedded NULs and control characters.
        return string.Concat(value.Where(c => !char.IsControl(c))).Trim();
    }

    private static string? Cpu()
    {
        foreach (var cpu in Query("SELECT Name, MaxClockSpeed FROM Win32_Processor"))
        {
            string name = Str(cpu, "Name") ?? "unknown";
            string speed = uint.TryParse(Str(cpu, "MaxClockSpeed"), out uint mhz)
                ? $" @ {mhz / 1000.0:0.0#} GHz"
                : "";
            return name + speed;
        }
        return null;
    }

    private static string? Motherboard()
    {
        string? board = null;
        foreach (var b in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            board = $"{Str(b, "Manufacturer")} {Str(b, "Product")}".Trim();
        foreach (var bios in Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
        {
            string? version = Str(bios, "SMBIOSBIOSVersion");
            if (board is not null && version is not null)
                board += $" (firmware {version})";
        }
        return board;
    }

    private static string? Ram()
    {
        // Group identical sticks: "4x Corsair CMK... DDR5-6000 (128 GB total)".
        var sticks = new List<(string Description, ulong Bytes)>();
        foreach (var m in Query("SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            string maker = Str(m, "Manufacturer") ?? "unknown";
            string part = Str(m, "PartNumber") ?? "";
            string type = DdrType(Str(m, "SMBIOSMemoryType"));
            string speed = uint.TryParse(Str(m, "ConfiguredClockSpeed"), out uint clock) && clock > 0
                ? clock.ToString()
                : Str(m, "Speed") ?? "?";
            ulong bytes = ulong.TryParse(Str(m, "Capacity"), out ulong c) ? c : 0;
            string description = string.Join(' ',
                $"{maker} {part} {type}-{speed}".Split(' ', StringSplitOptions.RemoveEmptyEntries));
            sticks.Add((description, bytes));
        }
        if (sticks.Count == 0)
            return null;

        var groups = sticks.GroupBy(s => s.Description)
            .Select(g => $"{g.Count()}x {g.Key}");
        ulong totalGb = sticks.Aggregate(0UL, (sum, s) => sum + s.Bytes) / (1024UL * 1024 * 1024);
        return $"{string.Join("; ", groups)} ({totalGb} GB total)";
    }

    private static string DdrType(string? smbiosType) => smbiosType switch
    {
        "20" => "DDR",
        "21" => "DDR2",
        "24" => "DDR3",
        "26" => "DDR4",
        "34" => "DDR5",
        null or "" or "0" => "RAM",
        _ => $"type {smbiosType}",
    };

    private static string? Storage()
    {
        var disks = new List<string>();
        foreach (var d in Query("SELECT Model, Size FROM Win32_DiskDrive"))
        {
            // Skip phantom drives (monitor USB hubs, card readers, virtual disks report ~0 GB).
            if (!ulong.TryParse(Str(d, "Size"), out ulong bytes) || bytes < 1_000_000_000)
                continue;
            disks.Add($"{Str(d, "Model") ?? "unknown"} ({bytes / 1_000_000_000} GB)");
        }
        return disks.Count > 0 ? string.Join("; ", disks) : null;
    }

    private static string? WindowsVersion()
    {
        string caption = "";
        foreach (var os in Query("SELECT Caption FROM Win32_OperatingSystem"))
            caption = Str(os, "Caption") ?? "";

        string display = "", build = Environment.OSVersion.Version.Build.ToString(), revision = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion")?.ToString() ?? "";
            build = key?.GetValue("CurrentBuildNumber")?.ToString() ?? build;
            revision = key?.GetValue("UBR")?.ToString() ?? "";
        }
        catch
        {
            // registry probe is optional
        }

        string buildText = revision.Length > 0 ? $"{build}.{revision}" : build;
        return $"{caption} {display} (build {buildText})".Replace("  ", " ").Trim();
    }
}
