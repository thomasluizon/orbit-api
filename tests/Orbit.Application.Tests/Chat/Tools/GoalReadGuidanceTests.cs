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

        tool.Description.Should().Contain("This result completely answers a goal-progress request; no habit analytics or habit calendar read is needed.");
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
