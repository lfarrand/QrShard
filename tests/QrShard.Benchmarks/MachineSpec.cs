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
            Try(rows, "Cores", Cores);
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
        if (!rows.Any(r => r.Item1 == "Cores"))
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
        foreach (var cpu in Query("SELECT Name, MaxClockSpeed, Description, Revision FROM Win32_Processor"))
        {
            string name = Str(cpu, "Name") ?? "unknown";
            string speed = uint.TryParse(Str(cpu, "MaxClockSpeed"), out uint mhz)
                ? $" @ {mhz / 1000.0:0.0#} GHz"
                : "";

            // Description carries the CPUID identity: "AMD64 Family 26 Model 68 Stepping 0".
            var details = new List<string>();
            var fms = System.Text.RegularExpressions.Regex.Match(
                Str(cpu, "Description") ?? "", @"Family (\d+) Model (\d+) Stepping (\d+)");
            if (fms.Success)
                details.Add($"family {fms.Groups[1].Value}, model {fms.Groups[2].Value}, stepping {fms.Groups[3].Value}");
            if (ushort.TryParse(Str(cpu, "Revision"), out ushort revision))
                details.Add($"revision 0x{revision:X4}");

            return name + speed + (details.Count > 0 ? $" ({string.Join(", ", details)})" : "");
        }
        return null;
    }

    private static string? Cores()
    {
        int physical = 0, logical = 0;
        foreach (var cpu in Query("SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            physical += int.TryParse(Str(cpu, "NumberOfCores"), out int c) ? c : 0;
            logical += int.TryParse(Str(cpu, "NumberOfLogicalProcessors"), out int l) ? l : 0;
        }
        return physical > 0 ? $"{physical} physical / {logical} logical" : null;
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
            string maker = DecodeRamMaker(Str(m, "Manufacturer"));
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

    /// <summary>
    /// Some firmwares report the module maker's raw JEDEC manufacturer ID instead of a name;
    /// decode the common ones. (When the firmware writes literally "Unknown" into SMBIOS —
    /// which some boards do — the maker is only readable from the DIMM SPD over SMBus, which
    /// needs a kernel driver a la CPU-Z; that is out of scope for a benchmark reporter.)
    /// </summary>
    private static string DecodeRamMaker(string? raw)
    {
        string maker = (raw ?? "").Trim();
        return maker.ToUpperInvariant() switch
        {
            "80AD" or "AD00" or "00AD" => "SK Hynix",
            "802C" or "2C00" or "002C" => "Micron",
            "80CE" or "CE00" or "00CE" => "Samsung",
            "859B" or "9B85" => "Crucial",
            "029E" or "9E02" => "Corsair",
            "0198" or "9801" => "Kingston",
            "04CB" or "CB04" => "ADATA",
            "" => "Unknown",
            _ => maker,
        };
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

    /// <summary>
    /// Only the drives the benchmark actually touched: the temp volume (where payloads are
    /// generated and shards encoded/decoded) and the artifacts volume (where results land),
    /// resolved to their physical disks via the logical-disk/partition/drive associations.
    /// </summary>
    private static string? Storage()
    {
        var roles = new Dictionary<string, List<string>>(); // "model|letter" -> roles
        foreach (var (path, role) in new[]
                 {
                     (Path.GetTempPath(), "temp/work"),
                     (Environment.CurrentDirectory, "artifacts"),
                 })
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null || root.Length < 2 || root[1] != ':')
                continue;
            string letter = root[..2];
            string? disk = PhysicalDiskFor(letter);
            if (disk is null)
                continue;
            string key = $"{disk} ({letter} ";
            if (!roles.TryGetValue(key, out var list))
                roles[key] = list = [];
            if (!list.Contains(role))
                list.Add(role);
        }
        return roles.Count > 0
            ? string.Join("; ", roles.Select(r => r.Key + string.Join(", ", r.Value) + ")"))
            : null;
    }

    private static string? PhysicalDiskFor(string driveLetter)
    {
        foreach (var partition in Query(
                     $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveLetter}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
        {
            string? partitionId = Str(partition, "DeviceID");
            if (partitionId is null)
                continue;
            foreach (var drive in Query(
                         $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
            {
                string model = Str(drive, "Model") ?? "unknown";
                string size = ulong.TryParse(Str(drive, "Size"), out ulong bytes)
                    ? $" {bytes / 1_000_000_000} GB"
                    : "";
                return model + size;
            }
        }
        return null;
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
