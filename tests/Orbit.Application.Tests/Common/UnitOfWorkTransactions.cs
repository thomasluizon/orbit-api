using NSubstitute;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

internal static class UnitOfWorkTransactions
{
    public static IUnitOfWork PassThroughTransactions(this IUnitOfWork unitOfWork)
    {
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        return unitOfWork;
    }

    public static IUnitOfWork PassThroughTransactions<T>(this IUnitOfWork unitOfWork)
    {
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<T>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<T>>>(0)(call.ArgAt<CancellationToken>(1)));
        return unitOfWork;
    }
}
