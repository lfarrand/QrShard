using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace QrShard;

/// <summary>
/// Produces a stable physical spelling for filesystem paths. Unlike <see cref="Path.GetFullPath(string)"/>,
/// this resolves symbolic-link and junction aliases in every existing path component.
/// </summary>
internal static class PhysicalPath
{
    private const int MaxUnixLinkResolutions = 40;

    internal static string Canonicalize(string path) => OperatingSystem.IsWindows()
        ? CanonicalWindowsPath(path)
        : CanonicalUnixPath(path);

    private static string CanonicalUnixPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)
            ?? throw new ArgumentException("Path has no filesystem root.", nameof(path));
        string current = root;
        var pending = new Queue<string>();
        EnqueueSegments(full[root.Length..], pending);
        int followedLinks = 0;

        while (pending.TryDequeue(out string? segment))
        {
            string candidate = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                current = candidate;
                while (pending.TryDequeue(out segment))
                    current = Path.Combine(current, segment);
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = candidate;
                continue;
            }

            followedLinks++;
            if (followedLinks > MaxUnixLinkResolutions)
                throw new IOException($"Too many symbolic links while resolving '{ShardHeader.Display(path)}'.");

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: false);
            if (target is null)
                throw new IOException($"Could not resolve reparse-point path '{ShardHeader.Display(candidate)}'.");

            // The target can itself contain a linked parent. Restart at its root so every target
            // component is examined before the original suffix is appended.
            string[] suffix = pending.ToArray();
            pending.Clear();
            string targetFull = Path.GetFullPath(target.FullName);
            root = Path.GetPathRoot(targetFull)
                ?? throw new IOException($"Resolved path '{ShardHeader.Display(targetFull)}' has no filesystem root.");
            current = root;
            EnqueueSegments(targetFull[root.Length..], pending);
            foreach (string remaining in suffix)
                pending.Enqueue(remaining);
        }

        current = Path.GetFullPath(current);
        if (!string.Equals(current, Path.GetPathRoot(current), StringComparison.Ordinal))
            current = Path.TrimEndingDirectorySeparator(current);
        return current;
    }

    private static void EnqueueSegments(string relative, Queue<string> pending)
    {
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
            pending.Enqueue(segment);
    }

    private static string CanonicalWindowsPath(string path)
    {
        string full = Path.GetFullPath(NormalizeWindowsDevicePath(path));
        var suffix = new Stack<string>();
        string existing = full;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing)
                throw new IOException($"Could not resolve path root for '{ShardHeader.Display(path)}'.");
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }

        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFileW(existing, desiredAccess: 0, shareReadWriteDelete,
            securityAttributes: 0, openExisting, backupSemantics, templateFile: 0);
        if (handle.IsInvalid)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        uint needed = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (needed == 0 || needed > 32_768)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        var resolvedBuffer = new char[needed];
        uint written = GetFinalPathNameByHandleW(handle, resolvedBuffer, (uint)resolvedBuffer.Length, 0);
        if (written == 0 || written >= resolvedBuffer.Length)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        string resolved = NormalizeLoopbackAdminShare(
            NormalizeWindowsDevicePath(new string(resolvedBuffer, 0, (int)written)));
        while (suffix.TryPop(out string? segment))
            resolved = Path.Combine(resolved, segment);
        resolved = Path.GetFullPath(resolved);
        if (!string.Equals(resolved, Path.GetPathRoot(resolved), StringComparison.OrdinalIgnoreCase))
            resolved = Path.TrimEndingDirectorySeparator(resolved);
        return resolved;
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string extended = @"\\?\";
        const string extendedUnc = @"\\?\UNC\";
        if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUnc.Length..];
        if (path.StartsWith(extended, StringComparison.OrdinalIgnoreCase))
        {
            string remainder = path[extended.Length..];
            if (remainder.Length >= 3 && char.IsAsciiLetter(remainder[0]) && remainder[1] == ':' &&
                (remainder[2] == '\\' || remainder[2] == '/'))
                return remainder;
            throw new ArgumentException(
                "Windows device/volume paths are not accepted for sessions, captures or output; use a drive or UNC path.");
        }
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Windows device paths are not accepted for sessions, captures or output; use a drive or UNC path.");
        return path;
    }

    private static string NormalizeLoopbackAdminShare(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        string[] parts = path[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1].Length != 2 || parts[1][1] != '$' ||
            !char.IsAsciiLetter(parts[1][0]))
            return path;
        string server = parts[0];
        bool loopback = server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (!loopback)
            return path;
        string local = parts[1][0] + @":\";
        return parts.Length == 2 ? local : Path.Combine([local, .. parts[2..]]);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[]? path,
        uint pathLength, uint flags);
}
