using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ResultExtensions
{
    public static Result<T> PropagateError<T>(this Result source)
    {
        return source.ErrorCode == Result.PayGateErrorCode
            ? Result.PayGateFailure<T>(source.Error)
            : source.ErrorCode is not null
                ? Result.Failure<T>(source.Error, source.ErrorCode)
                : Result.Failure<T>(source.Error);
    }

    public static Result PropagateError(this Result source)
    {
        return source.ErrorCode == Result.PayGateErrorCode
            ? Result.PayGateFailure(source.Error)
            : source.ErrorCode is not null
                ? Result.Failure(source.Error, source.ErrorCode)
                : Result.Failure(source.Error);
    }
}
