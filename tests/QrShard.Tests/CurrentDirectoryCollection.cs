namespace QrShard.Tests;

/// <summary>
/// Tests that change <see cref="Environment.CurrentDirectory"/> or process-wide environment
/// variables must not run alongside anything else. xUnit runs test classes in parallel by default,
/// so one class restoring process state while another was mid-assert made tests fail intermittently
/// — observed as 2 failures in one run of the same filter and build that passed twice after.
///
/// Some affected tests exercise decode without an explicit output path; others deliberately verify
/// environment-controlled behavior. Serialising them and restoring state in <c>finally</c> blocks
/// is therefore part of the test contract.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "current-directory";
}
