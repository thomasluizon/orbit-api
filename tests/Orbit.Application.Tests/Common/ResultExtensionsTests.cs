using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Common;

public class ResultExtensionsTests
{
    [Fact]
    public void PropagateErrorT_PayGate_ReturnsPayGateFailure()
    {
        // Arrange
        var source = Result.PayGateFailure("Upgrade required");

        // Act
        var result = source.PropagateError<int>();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("Upgrade required");
    }

    [Fact]
    public void PropagateErrorT_Regular_ReturnsFailure()
    {
        // Arrange
        var source = Result.Failure("Something went wrong");

        // Act
        var result = source.PropagateError<int>();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Error.Should().Be("Something went wrong");
    }

    [Fact]
    public void PropagateError_PayGate_ReturnsPayGateFailure()
    {
        // Arrange
        var source = Result.PayGateFailure("Upgrade required");

        // Act
        var result = source.PropagateError();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("Upgrade required");
    }

    [Fact]
    public void PropagateError_Regular_ReturnsFailure()
    {
        // Arrange
        var source = Result.Failure("Something went wrong");

        // Act
        var result = source.PropagateError();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Error.Should().Be("Something went wrong");
    }
}
