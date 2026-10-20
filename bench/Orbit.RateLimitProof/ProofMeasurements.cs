namespace Orbit.RateLimitProof;

public sealed record ProofSettings(string ConnectionString, string ExpectedDatabase)
{
    public static ProofSettings Parse(string? connectionString, string? expectedDatabase, string? disposable)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(expectedDatabase) || disposable != "yes")
            throw new InvalidOperationException("The disposable PostgreSQL proof requires connection, expected database and explicit acknowledgement.");
        return new ProofSettings(connectionString, expectedDatabase);
    }

    public override string ToString() => "Disposable PostgreSQL proof settings (credentials withheld)";
}

public sealed record ProofTiming(double TotalMs, double P50Ms, double P95Ms, double MaxMs, double RequestsPerSecond)
{
    public static ProofTiming Measure(TimeSpan total, IEnumerable<TimeSpan> acquisitions)
    {
        var sorted = acquisitions.Select(elapsed => elapsed.TotalMilliseconds).Order().ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(sorted.Length);
        if (total <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(total));
        return new ProofTiming(total.TotalMilliseconds, sorted[(int)Math.Ceiling(sorted.Length * 0.50) - 1],
            sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1], sorted.Length / total.TotalSeconds);
    }
}

public sealed record AcquisitionSample(bool Allowed, bool Error, int Attempts, TimeSpan Elapsed);
