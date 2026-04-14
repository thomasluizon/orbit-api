using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

/// <summary>
/// Integration coverage for the merit-based streak-freeze earning system.
/// These tests touch the real DbContext and API endpoints, so they require a
/// running Postgres instance and a configured <c>ConnectionStrings:DefaultConnection</c>.
/// </summary>
[Collection("Sequential")]
public class StreakFreezeMeritFlowTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"freeze-merit-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public StreakFreezeMeritFlowTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StreakInfo_FreshUser_ReportsZeroBalance()
    {
        var response = await _client.GetAsync("/api/gamification/streak");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var info = await response.Content.ReadFromJsonAsync<StreakInfoDto>(JsonOptions);
        info.Should().NotBeNull();
        info!.StreakFreezeBalance.Should().Be(0);
        info.FreezesAvailable.Should().Be(0);
        info.MaxFreezesHeld.Should().Be(3);
        info.MaxFreezesPerMonth.Should().Be(3);
        info.IsAtHeldCap.Should().BeFalse();
    }

    [Fact]
    public async Task Freeze_WithZeroBalance_ReturnsError()
    {
        // Fresh user has no earned balance; activating should fail.
        var response = await _client.PostAsync("/api/gamification/streak/freeze", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DomainEarn_Reaches7DayStreak_GrantsOneFreeze()
    {
        // Directly manipulate domain state to simulate a 7-day streak without logging.
        // Verifies the earn service and the persistence of the balance column.
        using var scope = _factory.Services.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IGenericRepository<User>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var me = await _client.GetFromJsonAsync<MeResponse>("/api/profile", JsonOptions);
        me.Should().NotBeNull();

        var user = await userRepo.FindOneTrackedAsync(u => u.Email == _email);
        user.Should().NotBeNull();
        user!.SetStreakState(7, 7, DateOnly.FromDateTime(DateTime.UtcNow));
        user.TryEarnStreakFreezes();
        await unitOfWork.SaveChangesAsync();

        var response = await _client.GetAsync("/api/gamification/streak");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var info = await response.Content.ReadFromJsonAsync<StreakInfoDto>(JsonOptions);
        info.Should().NotBeNull();
        info!.StreakFreezeBalance.Should().Be(1);
        info.CurrentStreak.Should().Be(7);
        info.ProgressToNextFreeze.Should().Be(0);
    }

    private sealed record StreakInfoDto(
        int CurrentStreak,
        int LongestStreak,
        DateOnly? LastActiveDate,
        int FreezesUsedThisMonth,
        int FreezesAvailable,
        int MaxFreezesPerMonth,
        int MaxFreezesHeld,
        int StreakFreezeBalance,
        int DaysUntilNextFreeze,
        int ProgressToNextFreeze,
        bool IsAtHeldCap,
        bool IsFrozenToday,
        IReadOnlyList<DateOnly> RecentFreezeDates);

    private sealed record MeResponse(string Name, string Email);
}
