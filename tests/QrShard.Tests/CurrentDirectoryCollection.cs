namespace QrShard.Tests;

/// <summary>
/// Tests that change <see cref="Environment.CurrentDirectory"/> must not run alongside anything
/// else. It is process-wide state, and xUnit runs test classes in parallel by default, so one
/// class restoring the directory while another was mid-assert made them fail intermittently —
/// observed as 2 failures in one run of the same filter and build that passed twice after.
///
/// The affected tests exercise the decode path where no explicit output path is given, which is
/// defined in terms of the current directory, so mutating it is inherent rather than incidental.
/// Serialising them is therefore the fix rather than rewriting them to avoid it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "current-directory";
}
