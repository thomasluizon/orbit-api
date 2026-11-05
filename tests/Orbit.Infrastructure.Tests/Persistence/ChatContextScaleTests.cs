using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ChatContextScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidates_TranslateNarrowProjectionForPostgres(bool placement)
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=translation_only").Options);
        var userId = Guid.NewGuid();

        var sql = ChatHabitCandidate.Select(db.Habits.AsNoTracking().Where(habit => habit.UserId == userId),
            new DateOnly(2026, 4, 3), placement).ToQueryString();

        sql.Should().Contain("EXISTS").And.Contain("\"Title\"").And.Contain("\"UserId\"")
            .And.NotContain("JOIN").And.NotContain("\"Description\"").And.NotContain("\"ReminderTimes\"")
            .And.NotContain("\"Note\"");
        if (!placement) sql.Should().NotContain("h.\"ChecklistItems\"");
        output.WriteLine(sql);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(2000)]
    public Task RankingTurn_BoundsContextHydrationAndHistory(int habitCount) => new ChatRankingWorkload(output).RunAsync(habitCount);
}
