using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;

namespace Orbit.Application.Common;

public static class ValidationCopyExtensions
{
    public static IRuleBuilderOptions<T, TProperty> WithCopy<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, string code, params object?[] args) =>
        rule.WithCopy(code, _ => args);

    public static IRuleBuilderOptions<T, TProperty> WithCopy<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, string code, Func<T, object?[]> args) =>
        rule.WithErrorCode(code)
            .WithMessage(instance => Resolve(code, false, args(instance)))
            .WithState(instance => new ValidationCopyArguments(args(instance)));

    public static string LocalizedMessage(this ValidationFailure failure, bool isPtBr)
    {
        var args = failure.CustomState is ValidationCopyArguments copyArgs ? copyArgs.Values : [];
        if (!ErrorCopy.TryResolve(failure.ErrorCode, isPtBr, args, out var template))
            return failure.ErrorMessage;

        var formatter = new MessageFormatter();
        if (failure.FormattedMessagePlaceholderValues is not null)
        {
            foreach (var (key, value) in failure.FormattedMessagePlaceholderValues)
                formatter.AppendArgument(key, value);
        }

        return formatter.BuildMessage(template);
    }

    private static string Resolve(string code, bool isPtBr, IReadOnlyList<object?> args) =>
        ErrorCopy.TryResolve(code, isPtBr, args, out var message)
            ? message
            : throw new InvalidOperationException($"Validation code has no user-facing copy: {code}");

    private sealed record ValidationCopyArguments(IReadOnlyList<object?> Values);
}
