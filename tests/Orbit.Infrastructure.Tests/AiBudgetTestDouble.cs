using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests;

internal static class AiBudgetTestDouble
{
    public static BudgetedAiCompletionClient Wrap(AiCompletionClient client)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
        var estimates = Substitute.For<IAiRequestCostEstimator>();
        estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
        return new BudgetedAiCompletionClient(client, Substitute.For<IAiDailyBudget>(), http, NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
    }
}
