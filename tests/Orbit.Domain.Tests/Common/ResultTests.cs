using FluentAssertions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccessTrue()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Success_ErrorIsEmpty()
    {
        var result = Result.Success();

        result.Error.Should().BeEmpty();
    }

    [Fact]
    public void Failure_IsFailureTrue()
    {
        var result = Result.Failure("something went wrong");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Failure_CarriesErrorMessage()
    {
        var result = Result.Failure("something went wrong");

        result.Error.Should().Be("something went wrong");
    }

    [Fact]
    public void Failure_EmptyMessage_ThrowsInvalidOp()
    {
        var act = () => Result.Failure(string.Empty);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*failed result must carry an error*");
    }

    [Fact]
    public void Success_WithError_ThrowsInvalidOp()
    {
        // The protected constructor is invoked indirectly via a subclass hack,
        // but we can test via the generic factory which calls the same constructor.
        // Result(true, "oops") should throw.
        var act = () => new TestResult(true, "oops");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*successful result cannot carry an error*");
    }

    [Fact]
    public void PayGateFailure_SetsErrorCodePayGate()
    {
        var result = Result.PayGateFailure("upgrade required");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("upgrade required");
    }

    [Fact]
    public void SuccessT_ValueAccessible()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void FailureT_ValueAccess_ThrowsInvalidOp()
    {
        var result = Result.Failure<int>("bad");

        var act = () => result.Value;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot access the value of a failed result*");
    }

    [Fact]
    public void PayGateFailureT_SetsErrorCode()
    {
        var result = Result.PayGateFailure<string>("upgrade required");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        result.Error.Should().Be("upgrade required");
    }

    /// <summary>
    /// Exposes the protected Result constructor for testing invalid argument combos.
    /// </summary>
    private class TestResult : Result
    {
        public TestResult(bool isSuccess, string error) : base(isSuccess, error) { }
    }
}
