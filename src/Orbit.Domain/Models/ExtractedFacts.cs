namespace Orbit.Domain.Models;

public record ExtractedFacts
{
    public required List<FactCandidate> Facts { get; init; }
}

public record FactCandidate
{
    public required string FactText { get; init; }
    public required string Category { get; init; }
}
