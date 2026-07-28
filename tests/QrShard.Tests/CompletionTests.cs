using System.Text.RegularExpressions;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The bash and PowerShell completions in completions/ are a hand-maintained copy of
/// <see cref="Cli.ArgSpecs"/>. Nothing at build time links them, so without this they drift the
/// moment an option is added: the completion silently stops offering it, or keeps offering one
/// the CLI now rejects as unknown — the worse direction, since it suggests an option that errors.
/// </summary>
public class CompletionTests
{
    // "send" is `encode` with two flags forced on, so it shares encode's surface and has no
    // ArgSpec of its own; the completions list it explicitly.
    private const string SendAlias = "send";

    [Fact]
    public void BashCompletion_OffersExactlyTheOptionsTheCliAccepts()
    {
        var parsed = ParseBash(File.ReadAllText(Path.Combine(SolutionRoot(), "completions", "qrshard.bash")));
        AssertMatchesArgSpecs(parsed, "qrshard.bash");
        Assert.Equal(Expected("encode"), parsed[SendAlias]); // send completes like encode
    }

    [Fact]
    public void PowerShellCompletion_OffersExactlyTheOptionsTheCliAccepts()
    {
        var parsed = ParsePowerShell(File.ReadAllText(Path.Combine(SolutionRoot(), "completions", "qrshard.ps1")));
        AssertMatchesArgSpecs(parsed, "qrshard.ps1");
    }

    private static void AssertMatchesArgSpecs(Dictionary<string, HashSet<string>> parsed, string file)
    {
        Assert.NotEmpty(parsed); // a parser that silently matched nothing would pass everything

        foreach (var (command, spec) in Cli.ArgSpecs)
        {
            Assert.True(parsed.ContainsKey(command), $"{file} has no completions for '{command}'");
            var expected = new HashSet<string>([.. spec.Options, .. spec.Flags], StringComparer.Ordinal);
            Assert.Equal(expected, parsed[command]);
        }

        foreach (string command in parsed.Keys)
            Assert.True(command == SendAlias || Cli.ArgSpecs.ContainsKey(command),
                $"{file} completes a command '{command}' the CLI does not define");
    }

    private static HashSet<string> Expected(string command)
    {
        var spec = Cli.ArgSpecs[command];
        return new HashSet<string>([.. spec.Options, .. spec.Flags], StringComparer.Ordinal);
    }

    /// <summary>Reads each `cmd|cmd)` case label together with the compgen word list under it.</summary>
    private static Dictionary<string, HashSet<string>> ParseBash(string text)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pairs = Regex.Matches(text,
            "(?m)^\\s*([a-z|]+)\\)\\s*\\r?\\n\\s*COMPREPLY=\\(\\s*\\$\\(compgen -W \"([^\"]*)\"");
        foreach (Match pair in pairs)
        {
            var words = pair.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string command in pair.Groups[1].Value.Split('|'))
                map[command] = [.. words];
        }
        return map;
    }

    /// <summary>Reads the $options hashtable, following the multi-line entries (trailing comma).</summary>
    private static Dictionary<string, HashSet<string>> ParsePowerShell(string text)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? command = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            var entry = Regex.Match(line, @"^(\w+)\s*=\s*(.*)$"); // hashtable keys are bare, values $-prefixed
            if (entry.Success)
            {
                command = entry.Groups[1].Value;
                map[command] = [];
                line = entry.Groups[2].Value;
            }
            else if (command is null)
            {
                continue;
            }

            foreach (Match token in Regex.Matches(line, "'([^']*)'"))
                map[command].Add(token.Groups[1].Value);
            if (!line.EndsWith(','))
                command = null;
        }
        // `send = $null` is filled in programmatically from encode, so it parses as empty.
        map.Remove(SendAlias);
        return map;
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QrShard.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
