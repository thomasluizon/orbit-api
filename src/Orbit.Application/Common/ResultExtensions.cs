using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ResultExtensions
{
    public static Result<T> PropagateError<T>(this Result source) =>
        Result.Failure<T>(source.Error, source.ErrorCode, source.ErrorArgs);

    public static Result PropagateError(this Result source) =>
        Result.Failure(source.Error, source.ErrorCode, source.ErrorArgs);
}
