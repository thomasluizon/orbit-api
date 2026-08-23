using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class FoundingAchievementReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileCandidatesAsync_UsesBoundedCursorPagesAndProcessesEveryCandidate()
    {
        var reader = Substitute.For<IFoundingAchievementReader>();
        var gamificationService = Substitute.For<IGamificationService>();
        gamificationService.ReconcileFoundingAchievementsAsync(
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var firstPage = Enumerable.Range(0, FoundingAchievementReconciliationService.PageSize)
            .Select(index => new FoundingAchievementCursor(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"),
                new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index)))
            .ToList();
        var finalCandidate = new FoundingAchievementCursor(
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            new DateTime(2026, 8, 1, 0, 2, 0, DateTimeKind.Utc));
        reader.ReadCandidatePageAsync(
                null,
                FoundingAchievementReconciliationService.PageSize,
                Arg.Any<CancellationToken>())
            .Returns(firstPage);
        reader.ReadCandidatePageAsync(
                firstPage[^1],
                FoundingAchievementReconciliationService.PageSize,
                Arg.Any<CancellationToken>())
            .Returns(new[] { finalCandidate });

        using var provider = new ServiceCollection()
            .AddSingleton(reader)
            .AddSingleton(gamificationService)
            .BuildServiceProvider();
        var service = new FoundingAchievementReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FoundingAchievementReconciliationService>.Instance,
            new ConfigurationBuilder().Build());

        var granted = await service.ReconcileCandidatesAsync(CancellationToken.None);

        granted.Should().Be(0);
        await gamificationService.Received(FoundingAchievementReconciliationService.PageSize + 1)
            .ReconcileFoundingAchievementsAsync(
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>());
        await reader.Received(1).ReadCandidatePageAsync(
            firstPage[^1],
            FoundingAchievementReconciliationService.PageSize,
            Arg.Any<CancellationToken>());
    }
}
