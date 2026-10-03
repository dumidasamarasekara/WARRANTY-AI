namespace Warranty.Application.Abstractions.Adjudication;

/// <summary>Runs (or resumes) the adjudication harness for one claim round in the current tenant scope.</summary>
public interface IAdjudicationRunner
{
    Task RunAsync(Guid claimId, int round, CancellationToken ct);
}
