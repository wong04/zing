namespace Zing.Core.Tests;

/// <summary>
/// Classes that hold code to a wall-clock budget. They run alone, after every other collection: measured while the rest of
/// the suite ran in parallel (engine processes, loopback servers) on a two-core runner, a budget a quiet machine meets many
/// times over was missed now and then.
/// </summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public sealed class TimingCollection;
