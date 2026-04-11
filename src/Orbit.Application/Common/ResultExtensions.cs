using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ResultExtensions
{
    public static Result<T> PropagateError<T>(this Result source)
    {
        return source.ErrorCode == Result.PayGateErrorCode
            ? Result.PayGateFailure<T>(source.Error)
            : Result.Failure<T>(source.Error);
    }

    public static Result PropagateError(this Result source)
    {
        return source.ErrorCode == Result.PayGateErrorCode
            ? Result.PayGateFailure(source.Error)
            : Result.Failure(source.Error);
    }
}
