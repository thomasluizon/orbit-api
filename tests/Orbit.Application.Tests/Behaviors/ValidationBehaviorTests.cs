using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using NSubstitute;
using Orbit.Application.Behaviors;

namespace Orbit.Application.Tests.Behaviors;

public record ValidationTestRequest(string Name) : IRequest<string>;

public class ValidationBehaviorTests
{
    private readonly RequestHandlerDelegate<string> _next = Substitute.For<RequestHandlerDelegate<string>>();

    public ValidationBehaviorTests()
    {
        _next.Invoke().Returns("success");
    }

    [Fact]
    public async Task Handle_NoValidators_CallsNext()
    {
        // Arrange
        var validators = Enumerable.Empty<IValidator<ValidationTestRequest>>();
        var behavior = new ValidationBehavior<ValidationTestRequest, string>(validators);
        var request = new ValidationTestRequest("test");

        // Act
        var result = await behavior.Handle(request, _next, CancellationToken.None);

        // Assert
        result.Should().Be("success");
        await _next.Received(1).Invoke();
    }

    [Fact]
    public async Task Handle_ValidInput_CallsNext()
    {
        // Arrange
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        var behavior = new ValidationBehavior<ValidationTestRequest, string>(new[] { validator });
        var request = new ValidationTestRequest("valid");

        // Act
        var result = await behavior.Handle(request, _next, CancellationToken.None);

        // Assert
        result.Should().Be("success");
        await _next.Received(1).Invoke();
    }

    [Fact]
    public async Task Handle_InvalidInput_ThrowsValidationException()
    {
        // Arrange
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult(failures));

        var behavior = new ValidationBehavior<ValidationTestRequest, string>(new[] { validator });
        var request = new ValidationTestRequest("");

        // Act
        var act = () => behavior.Handle(request, _next, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCount(1);
        await _next.DidNotReceive().Invoke();
    }

    [Fact]
    public async Task Handle_MultipleFailures_ThrowsAllErrors()
    {
        // Arrange
        var validator1 = Substitute.For<IValidator<ValidationTestRequest>>();
        validator1.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult(new[]
            {
                new ValidationFailure("Name", "Name is required")
            }));

        var validator2 = Substitute.For<IValidator<ValidationTestRequest>>();
        validator2.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult(new[]
            {
                new ValidationFailure("Name", "Name is too short")
            }));

        var behavior = new ValidationBehavior<ValidationTestRequest, string>(new[] { validator1, validator2 });
        var request = new ValidationTestRequest("");

        // Act
        var act = () => behavior.Handle(request, _next, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCount(2);
        await _next.DidNotReceive().Invoke();
    }
}
