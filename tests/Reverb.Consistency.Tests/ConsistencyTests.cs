using System.Collections.Concurrent;
using Reverb.Testing;

namespace Reverb.Consistency.Tests;

/// <summary>
/// The consistency invariant: after any sequence of edits and reactive runs, cell statuses,
/// outputs and stored values equal those of a fresh run-all of the final file.
/// </summary>
public class ConsistencyTests(ITestOutputHelper output)
{
    static string FuzzDirectory => Path.Combine(Snapshot.FixturesDirectory, "fuzz");

    static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    [Fact]
    public async Task Reactive_runs_match_a_fresh_run_all()
    {
        var count = EnvInt("REVERB_FUZZ_SEQUENCES", 500);
        var start = EnvInt("REVERB_FUZZ_SEED", 0);
        var failures = new ConcurrentBag<(Scenario Scenario, string Difference)>();
        var steps = 0;

        await Parallel.ForEachAsync(Enumerable.Range(start, count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            async (seed, _) =>
            {
                var scenario = new Fuzzer(seed).Generate(maxSteps: 8);
                Interlocked.Add(ref steps, scenario.Steps.Count);
                var result = await Checker.CheckAsync(scenario, checkEveryStep: seed % 5 == 0);
                if (!result.Consistent) failures.Add((scenario, result.Difference));
            });

        output.WriteLine($"{count} sequences, {steps} steps, {failures.Count} mismatches");
        if (failures.IsEmpty) return;

        var (first, _) = failures.OrderBy(f => f.Scenario.Steps.Count).ThenBy(f => f.Scenario.Seed).First();
        var small = await Checker.ShrinkAsync(first);
        var diff = (await Checker.CheckAsync(small)).Difference;
        Directory.CreateDirectory(FuzzDirectory);
        var path = Path.Combine(FuzzDirectory, $"seed-{small.Seed}.txt");
        await File.WriteAllTextAsync(path, Checker.Serialize(small));
        Assert.Fail($"{failures.Count} of {count} sequences broke the consistency invariant (seeds {string.Join(", ", failures.Select(f => f.Scenario.Seed).Order().Take(20))}). "
            + $"Smallest reproduction ({small.Steps.Count} steps) saved to {path}.\n{diff}");
    }

    public static TheoryData<string> SavedReproductions =>
        new(Directory.Exists(FuzzDirectory)
            ? Directory.EnumerateFiles(FuzzDirectory, "*.txt").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            : ["(none)"]);

    [Theory]
    [MemberData(nameof(SavedReproductions))]
    public async Task Saved_reproductions_stay_fixed(string file)
    {
        if (file == "(none)") return;
        var scenario = Checker.Deserialize(await File.ReadAllTextAsync(Path.Combine(FuzzDirectory, file)));
        var result = await Checker.CheckAsync(scenario, checkEveryStep: true);
        Assert.True(result.Consistent, result.Difference);
    }

    [Fact]
    public void Scenarios_are_deterministic_and_round_trip()
    {
        var a = new Fuzzer(42).Generate(8);
        var b = new Fuzzer(42).Generate(8);
        Assert.Equal(Checker.Serialize(a), Checker.Serialize(b));
        var replayed = Checker.Deserialize(Checker.Serialize(a));
        Assert.Equal(a.Steps.Count, replayed.Steps.Count);
    }
}
