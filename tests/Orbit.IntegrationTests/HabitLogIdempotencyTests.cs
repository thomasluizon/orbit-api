using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Infrastructure.Persistence;

namespace Orbit.IntegrationTests;

[Collection("Sequential")]
public class HabitLogIdempotencyTests : IAsyncLifetime
{
    private readonly IntegrationTestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _email = $"habitlog-idem-{Guid.NewGuid()}@integration.test";
    private const string TestCode = "999999";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HabitLogIdempotencyTests(IntegrationTestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        IntegrationTestHelpers.RegisterTestAccount(_email, TestCode);
    }

    public async Task InitializeAsync()
    {
        await IntegrationTestHelpers.AuthenticateWithCodeAsync(_client, _email, TestCode, JsonOptions);
    }

    public async Task DisposeAsync()
    {
        try
        {
            var habitsResponse = await _client.GetAsync(IntegrationTestHelpers.BuildHabitSchedulePath());
            if (habitsResponse.IsSuccessStatusCode)
            {
                var paginated = await habitsResponse.Content.ReadFromJsonAsync<PaginatedResponse<HabitDto>>(JsonOptions);
                foreach (var h in paginated?.Items?.DistinctBy(h => h.Id) ?? [])
                    await _client.DeleteAsync($"/api/habits/{h.Id}");
            }
        }
        finally
        {
            _client.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentLog_FlexibleHabitSameDate_SettlesToOneCompletionRow()
    {
        var habitId = await CreateFlexibleHabit("Concurrent flexible log");

        var firstLog = _client.PostAsJsonAsync($"/api/habits/{habitId}/log", new { });
        var secondLog = _client.PostAsJsonAsync($"/api/habits/{habitId}/log", new { });
        var responses = await Task.WhenAll(firstLog, secondLog);

        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<LogResponse>(JsonOptions);
            body!.LogId.Should().NotBeEmpty();
        }

        var completionCount = await CountCompletionsAsync(habitId);
        completionCount.Should().Be(1);
    }

    [Fact]
    public async Task PartialIndex_SkipAndCompletionCoexist_SecondCompletionRejected()
    {
        var habitId = await CreateFlexibleHabit("Partial index habit");
        var date = DateOnly.FromDateTime(DateTime.UtcNow);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var completionThenCoexistingSkip = async () =>
        {
            await InsertLogAsync(dbContext, habitId, date, value: 1);
            await InsertLogAsync(dbContext, habitId, date, value: 0);
        };
        await completionThenCoexistingSkip.Should().NotThrowAsync();

        var secondCompletion = async () => await InsertLogAsync(dbContext, habitId, date, value: 1);
        await secondCompletion.Should().ThrowAsync<DbException>();

        var completions = await dbContext.HabitLogs
            .CountAsync(l => l.HabitId == habitId && l.Date == date && l.Value > 0);
        completions.Should().Be(1);

        var skips = await dbContext.HabitLogs
            .CountAsync(l => l.HabitId == habitId && l.Date == date && l.Value == 0);
        skips.Should().Be(1);
    }

    [Fact]
    public async Task DedupMigrationSql_CollapsesDuplicateCompletions_AndPreservesSkip()
    {
        var habitId = await CreateFlexibleHabit("Dedup migration habit");
        var date = DateOnly.FromDateTime(DateTime.UtcNow);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        await dbContext.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_HabitLogs_HabitId_Date_Completed\";");

        try
        {
            await InsertCompletionRawAsync(dbContext, habitId, date);
            await InsertCompletionRawAsync(dbContext, habitId, date);
            await InsertLogAsync(dbContext, habitId, date, value: 0);

            var beforeCompletions = await dbContext.HabitLogs
                .CountAsync(l => l.HabitId == habitId && l.Date == date && l.Value > 0);
            beforeCompletions.Should().Be(2);

            await dbContext.Database.ExecuteSqlRawAsync(@"
                DELETE FROM ""HabitLogs"" a
                USING ""HabitLogs"" b
                WHERE a.""Id"" < b.""Id""
                  AND a.""HabitId"" = b.""HabitId""
                  AND a.""Date""    = b.""Date""
                  AND a.""Value"" > 0
                  AND b.""Value"" > 0;");

            var afterCompletions = await dbContext.HabitLogs
                .CountAsync(l => l.HabitId == habitId && l.Date == date && l.Value > 0);
            afterCompletions.Should().Be(1);

            var skips = await dbContext.HabitLogs
                .CountAsync(l => l.HabitId == habitId && l.Date == date && l.Value == 0);
            skips.Should().Be(1);

            var recreateAfterDedup = async () =>
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX \"IX_HabitLogs_HabitId_Date_Completed\" " +
                    "ON \"HabitLogs\" (\"HabitId\", \"Date\") WHERE \"Value\" > 0;");
            };
            await recreateAfterDedup.Should().NotThrowAsync();
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_HabitLogs_HabitId_Date_Completed\" " +
                "ON \"HabitLogs\" (\"HabitId\", \"Date\") WHERE \"Value\" > 0;");
        }
    }

    private async Task<int> CountCompletionsAsync(Guid habitId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return await dbContext.HabitLogs
            .CountAsync(l => l.HabitId == habitId && l.Value > 0);
    }

    private static async Task InsertLogAsync(OrbitDbContext dbContext, Guid habitId, DateOnly date, decimal value)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Date\", \"Value\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            Guid.NewGuid(), habitId, date, value, DateTime.UtcNow, DateTime.UtcNow);
    }

    private static async Task InsertCompletionRawAsync(OrbitDbContext dbContext, Guid habitId, DateOnly date)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Date\", \"Value\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            Guid.NewGuid(), habitId, date, 1m, DateTime.UtcNow, DateTime.UtcNow);
    }

    private async Task<Guid> CreateFlexibleHabit(string title)
    {
        var response = await _client.PostAsJsonAsync("/api/habits", new
        {
            title,
            type = "Boolean",
            frequencyUnit = "Week",
            frequencyQuantity = 3,
            isFlexible = true
        });

        response.EnsureSuccessStatusCode();
        return await IntegrationTestHelpers.ReadCreatedIdAsync(response, JsonOptions);
    }

    private record HabitDto(Guid Id, string Title);
    private record PaginatedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
    private record LogResponse(Guid LogId);
}
