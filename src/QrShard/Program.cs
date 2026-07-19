using System.Diagnostics.CodeAnalysis;

namespace QrShard;

[ExcludeFromCodeCoverage]
internal static class Program
{
    private static int Main(string[] args) => new Cli().Run(args);
}
