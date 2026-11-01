using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class GoalReadGuidanceTests
{
    [Fact]
    public void QueryGoals_AnswersGoalProgressCompletely()
    {
        var tool = new QueryGoalsTool(Substitute.For<IGenericRepository<Goal>>(),
            Substitute.For<IUserDateService>(), Substitute.For<IGoalProgressReadSyncer>());

        tool.Description.Should().Contain("For goals or progress, set include_completed=true unless only active goals or a specific status is requested.");
        tool.Description.Should().Contain("Progress is current value against target. For goal progress, call only query_goals with orbit_read_complete=true; no habit analytics or calendar read is needed.");
    }

    [Fact]
    public void HabitMetrics_RequiresAHabitIdAndDoesNotReportGoalProgress()
    {
        var tool = new GetHabitMetricsTool(Substitute.For<IMediator>());

        tool.Description.Should().Contain("This tool reads habit logs and does not report goal progress; use query_goals for goal progress.");
        tool.Description.Should().Contain("habit_id is a habit ID from the habit index; a goal ID is never a habit_id.");
    }

    [Fact]
    public void Profile_ReadsOnlyRequestedAccountSettings()
    {
        new GetProfileTool(Substitute.For<IMediator>()).Description.Should()
            .Contain("Use only for profile, plan or settings requests; this tool reports no goal or habit progress.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HabitAnalyticsAndCalendar_ReadLogsWithoutGoalProgress(bool month)
    {
        var mediator = Substitute.For<IMediator>();
        const string instruction = "This tool reads habit logs and does not report goal progress; use query_goals for goal progress.";

        var description = month ? new GetHabitMonthTool(mediator).Description : new GetPeriodAnalyticsTool(mediator).Description;
        description.Should().Contain(instruction);
    }
}
