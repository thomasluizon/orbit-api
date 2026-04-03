using FluentValidation;
using MediatR;

namespace Orbit.Application.Behaviors;

public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : class
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
#pragma warning disable CA2016 // RequestHandlerDelegate<T> does not accept a CancellationToken parameter
            return await next();
#pragma warning restore CA2016

        var context = new ValidationContext<TRequest>(request);

        var validationResults = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count != 0)
            throw new ValidationException(failures);

#pragma warning disable CA2016 // RequestHandlerDelegate<T> does not accept a CancellationToken parameter
        return await next();
#pragma warning restore CA2016
    }
}
