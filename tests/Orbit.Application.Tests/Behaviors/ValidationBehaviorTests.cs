using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using MediatR.Wrappers;
using Microsoft.Extensions.DependencyInjection;
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
        var validators = Enumerable.Empty<IValidator<ValidationTestRequest>>();
        var behavior = new ValidationBehavior<ValidationTestRequest, string>(validators);
        var request = new ValidationTestRequest("test");

        var result = await behavior.Handle(request, _next, CancellationToken.None);

        result.Should().Be("success");
        await _next.Received(1).Invoke();
    }

    [Fact]
    public async Task Handle_ValidInput_CallsNext()
    {
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        var behavior = new ValidationBehavior<ValidationTestRequest, string>(new[] { validator });
        var request = new ValidationTestRequest("valid");

        var result = await behavior.Handle(request, _next, CancellationToken.None);

        result.Should().Be("success");
        await _next.Received(1).Invoke();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_RequestToken_ReachesContinuation(bool hasValidator)
    {
        using var cancellation = new CancellationTokenSource();
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), cancellation.Token)
            .Returns(new ValidationResult());
        var behavior = new ValidationBehavior<ValidationTestRequest, string>(hasValidator ? [validator] : []);
        var observed = CancellationToken.None;
        Task<string> Next(CancellationToken token)
        {
            observed = token;
            return Task.FromResult("success");
        }

        var result = await behavior.Handle(new ValidationTestRequest("valid"), Next, cancellation.Token);

        result.Should().Be("success");
        observed.Should().Be(cancellation.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_MediatRWrapper_PreservesHandlerTokenAndResult(bool hasValidator)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = Substitute.For<IRequestHandler<ValidationTestRequest, string>>();
        handler.Handle(Arg.Any<ValidationTestRequest>(), cancellation.Token).Returns("success");
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), cancellation.Token)
            .Returns(new ValidationResult());
        var services = new ServiceCollection();
        services.AddSingleton(handler);
        services.AddSingleton<IPipelineBehavior<ValidationTestRequest, string>>(
            new ValidationBehavior<ValidationTestRequest, string>(hasValidator ? [validator] : []));
        using var provider = services.BuildServiceProvider();
        var request = new ValidationTestRequest("valid");

        var result = await new RequestHandlerWrapperImpl<ValidationTestRequest, string>()
            .Handle(request, provider, cancellation.Token);

        result.Should().Be("success");
        await handler.Received(1).Handle(request, cancellation.Token);
    }

    [Fact]
    public async Task Handle_InvalidInput_ThrowsValidationException()
    {
        var validator = Substitute.For<IValidator<ValidationTestRequest>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.ValidateAsync(Arg.Any<ValidationContext<ValidationTestRequest>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult(failures));

        var behavior = new ValidationBehavior<ValidationTestRequest, string>(new[] { validator });
        var request = new ValidationTestRequest("");

        var act = () => behavior.Handle(request, _next, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCount(1);
        await _next.DidNotReceive().Invoke();
    }

    [Fact]
    public async Task Handle_MultipleFailures_ThrowsAllErrors()
    {
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

        var act = () => behavior.Handle(request, _next, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCount(2);
        await _next.DidNotReceive().Invoke();
    }
}
