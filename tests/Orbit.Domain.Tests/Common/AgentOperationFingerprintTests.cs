using FluentAssertions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Tests.Common;

public class AgentOperationFingerprintTests
{
    [Fact]
    public void Compute_ReturnsFixedLengthHexRegardlessOfPayloadSize()
    {
        var largeArguments = $$"""{"habits":[{{string.Join(",", Enumerable.Range(1, 50).Select(index => $$"""{"title":"Habit {{index}} with a deliberately long descriptive title"}"""))}}]}""";

        var fingerprint = AgentOperationFingerprint.Compute("bulk_create_habits", largeArguments);

        fingerprint.Should().HaveLength(64);
        fingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public void Compute_IsDeterministicForIdenticalInput()
    {
        AgentOperationFingerprint.Compute("create_habit", """{"title":"Read"}""")
            .Should().Be(AgentOperationFingerprint.Compute("create_habit", """{"title":"Read"}"""));
    }

    [Fact]
    public void Compute_DiffersAcrossOperationsAndArguments()
    {
        var baseline = AgentOperationFingerprint.Compute("create_habit", """{"title":"Read"}""");

        AgentOperationFingerprint.Compute("delete_habit", """{"title":"Read"}""")
            .Should().NotBe(baseline);
        AgentOperationFingerprint.Compute("create_habit", """{"title":"Run"}""")
            .Should().NotBe(baseline);
    }
}
