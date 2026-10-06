using System.Collections.Concurrent;

namespace Warranty.IntegrationTests.Scenarios;

/// <summary>
/// Golden scenario claims shared by the test classes of the app collection: a scenario's claim is
/// submitted once per test run, by whichever class asks first, and every later caller reads that claim.
/// A second claim with the same serial and evidence would raise duplicate-serial and evidence-reuse
/// signals and change the scenario's outcome, so classes that read the same scenario (e.g. US1 and US6
/// on S1) go through here instead of keeping claims of their own.
/// </summary>
internal static class ScenarioClaims
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<Guid>>> Submitted = new(StringComparer.Ordinal);

    /// <summary>The scenario's claim ID; <paramref name="submit"/> runs only when no class has submitted the scenario yet.</summary>
    public static Task<Guid> SubmitOnceAsync(GoldenScenario scenario, Func<GoldenScenario, Task<Guid>> submit)
        => Submitted.GetOrAdd(scenario.ScenarioId, _ => new Lazy<Task<Guid>>(() => submit(scenario))).Value;
}
