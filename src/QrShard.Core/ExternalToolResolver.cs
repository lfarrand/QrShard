using System.Diagnostics;

namespace QrShard;

/// <summary>
/// Resolves helper programs to absolute paths before process creation. Passing a bare executable
/// name to Process.Start permits platform-specific current/application-directory probing ahead of
/// PATH, which lets an unrelated local file replace the intended tool.
/// </summary>
internal static class ExternalToolResolver
{
    internal static string? Resolve(string toolName, string? configuredPath = null)
    {
        if (string.IsNullOrWhiteSpace(toolName) || Path.GetFileName(toolName) != toolName)
            throw new ArgumentException("A helper-program name, without a path, is required.", nameof(toolName));

        if (configuredPath is not null)
        {
            if (!Path.IsPathFullyQualified(configuredPath))
                throw new InvalidOperationException($"The configured {toolName} path must be absolute.");
            string configured = Path.GetFullPath(configuredPath);
            return IsExecutableFile(configured) ? configured : null;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string current = Path.GetFullPath(Environment.CurrentDirectory);
        string application = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (string rawEntry in path.Split(Path.PathSeparator))
        {
            string entry = Environment.ExpandEnvironmentVariables(rawEntry.Trim().Trim('"'));
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry))
                continue;

            string directory;
            try
            {
                directory = Path.GetFullPath(entry);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // These locations are commonly writable by whoever supplied the input. They are also
            // the two implicit search locations this resolver exists to remove.
            if (IsSameOrChild(directory, current) || IsSameOrChild(directory, application))
                continue;

            foreach (string candidateName in CandidateNames(toolName))
            {
                string candidate = Path.Combine(directory, candidateName);
                if (IsExecutableFile(candidate))
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string absoluteExecutable)
    {
        if (!Path.IsPathFullyQualified(absoluteExecutable))
            throw new ArgumentException("The helper-program path must be absolute.", nameof(absoluteExecutable));

        string executable = Path.GetFullPath(absoluteExecutable);
        string directory = Path.GetDirectoryName(executable)
            ?? throw new ArgumentException("The helper-program path has no parent directory.", nameof(absoluteExecutable));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        start.Environment["PATH"] = RestrictedChildPath(directory);
        return start;
    }

    private static IEnumerable<string> CandidateNames(string toolName)
    {
        yield return toolName;
        if (OperatingSystem.IsWindows() && Path.GetExtension(toolName).Length == 0)
        {
            yield return toolName + ".exe";
            yield return toolName + ".com";
        }
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            const UnixFileMode anyExecute = UnixFileMode.UserExecute |
                                            UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherExecute;
            return (mode & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSameOrChild(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);
        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string RestrictedChildPath(string executableDirectory)
    {
        var directories = new List<string> { executableDirectory };
        if (OperatingSystem.IsWindows())
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (system.Length != 0)
                directories.Add(system);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length != 0)
                directories.Add(windows);
        }
        else
        {
            directories.AddRange(["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]);
        }
        return string.Join(Path.PathSeparator, directories.Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }
}
