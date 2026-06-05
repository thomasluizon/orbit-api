using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class ChatToolMetadataTests
{
    [Fact]
    public void ToolMetadata_ExposesExpectedNamesDescriptionsAndSchemas()
    {
        var mediator = Substitute.For<MediatR.IMediator>();
        var userDateService = Substitute.For<IUserDateService>();
        var payGateService = Substitute.For<IPayGateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var gamificationService = Substitute.For<IGamificationService>();
        var logger = Substitute.For<ILogger<UpdateGoalStatusTool>>();

        var assignTagsTool = new AssignTagsTool(Repo<Habit>(), Repo<Tag>(), unitOfWork);
        var bulkUpdateHabitEmojisTool = new BulkUpdateHabitEmojisTool(Repo<Habit>());
        var bulkLogHabitsTool = new BulkLogHabitsTool(Repo<Habit>(), Repo<HabitLog>(), userDateService);
        var bulkSkipHabitsTool = new BulkSkipHabitsTool(Repo<Habit>(), Repo<HabitLog>(), userDateService);
        var createGoalTool = new CreateGoalTool(Repo<Goal>(), unitOfWork);
        var createHabitTool = new CreateHabitTool(Repo<Habit>(), Repo<Tag>(), Repo<Goal>(), userDateService, payGateService, unitOfWork);
        var createSubHabitTool = new CreateSubHabitTool(mediator);
        var deleteGoalTool = new DeleteGoalTool(Repo<Goal>(), unitOfWork);
        var deleteHabitTool = new DeleteHabitTool(Repo<Habit>());
        var duplicateHabitTool = new DuplicateHabitTool(mediator);
        var getDailySummaryTool = new GetDailySummaryTool(mediator, userDateService);
        var getRetrospectiveTool = new GetRetrospectiveTool(mediator, userDateService);
        var getHabitMetricsTool = new GetHabitMetricsTool(mediator);
        var goalReviewTool = new GoalReviewTool(Repo<Goal>(), userDateService);
        var linkHabitsTool = new LinkHabitsToGoalTool(Repo<Goal>(), Repo<Habit>(), unitOfWork);
        var logHabitTool = new LogHabitTool(Repo<Habit>(), Repo<HabitLog>(), userDateService);
        var moveHabitTool = new MoveHabitTool(Repo<Habit>());
        var queryGoalsTool = new QueryGoalsTool(Repo<Goal>());
        var queryHabitsTool = new QueryHabitsTool(Repo<Habit>(), Repo<User>());
        var skipHabitTool = new SkipHabitTool(Repo<Habit>(), Repo<HabitLog>(), userDateService);
        var suggestBreakdownTool = new SuggestBreakdownTool();
        var updateGoalProgressTool = new UpdateGoalProgressTool(Repo<Goal>(), Repo<GoalProgressLog>(), unitOfWork);
        var updateGoalStatusTool = new UpdateGoalStatusTool(Repo<Goal>(), gamificationService, unitOfWork, logger);
        var updateGoalTool = new UpdateGoalTool(Repo<Goal>(), unitOfWork);
        var updateHabitTool = new UpdateHabitTool(Repo<Habit>());
        var listTagsTool = new ListTagsTool(mediator);
        var createTagTool = new CreateTagTool(mediator);
        var updateTagTool = new UpdateTagTool(mediator);
        var deleteTagTool = new DeleteTagTool(mediator);
        var reorderGoalsTool = new ReorderGoalsTool(mediator);
        var getReferralCodeTool = new GetReferralCodeTool(mediator);

        AssertTool(assignTagsTool, "assign_tags", "tag", "tag_names");
        JsonSerializer.Serialize(assignTagsTool.GetParameterSchema()).Should().Contain("tag_ids");
        AssertTool(listTagsTool, "list_tags", "tag", "type", expectReadOnly: true);
        AssertTool(createTagTool, "create_tag", "tag", "color");
        AssertTool(updateTagTool, "update_tag", "tag", "tag_id");
        AssertTool(deleteTagTool, "delete_tag", "tag", "tag_id");
        AssertTool(reorderGoalsTool, "reorder_goals", "position", "goal_id");
        AssertTool(getReferralCodeTool, "get_referral_code", "referral", "type");
        AssertTool(bulkUpdateHabitEmojisTool, "bulk_update_habit_emojis", "emojis", "infer_from_title");
        AssertTool(bulkLogHabitsTool, "bulk_log_habits", "multiple", "habit_ids");
        AssertTool(bulkSkipHabitsTool, "bulk_skip_habits", "multiple", "habit_ids");
        AssertTool(createGoalTool, "create_goal", "goal", "goal_type");
        AssertTool(createHabitTool, "create_habit", "habit", "checklist_items");
        AssertTool(createSubHabitTool, "create_sub_habit", "sub-habit", "parent_habit_id");
        AssertTool(deleteGoalTool, "delete_goal", "goal", "goal_id");
        AssertTool(deleteHabitTool, "delete_habit", "habit", "habit_id");
        AssertTool(duplicateHabitTool, "duplicate_habit", "duplicate", "habit_id");
        AssertTool(getDailySummaryTool, "get_daily_summary", "summary", "date_from", expectReadOnly: true);
        AssertTool(getRetrospectiveTool, "get_retrospective", "retrospective", "period", expectReadOnly: true);
        AssertTool(getHabitMetricsTool, "get_habit_metrics", "metrics", "habit_id", expectReadOnly: true);
        AssertTool(goalReviewTool, "review_goals", "review", "properties", expectReadOnly: true);
        AssertTool(linkHabitsTool, "link_habits_to_goal", "Link", "habit_ids");
        AssertTool(logHabitTool, "log_habit", "habit", "date");
        AssertTool(moveHabitTool, "move_habit", "parent", "new_parent_id");
        AssertTool(queryGoalsTool, "query_goals", "goals", "include_linked_habits", expectReadOnly: true);
        AssertTool(queryHabitsTool, "query_habits", "habits", "include_metrics", expectReadOnly: true);
        AssertTool(skipHabitTool, "skip_habit", "Skip", "date");
        AssertTool(suggestBreakdownTool, "suggest_breakdown", "Suggest", "suggested_sub_habits");
        AssertTool(updateGoalProgressTool, "update_goal_progress", "goal", "current_value");
        AssertTool(updateGoalStatusTool, "update_goal_status", "goal", "status");
        AssertTool(updateGoalTool, "update_goal", "goal", "target_value");
        AssertTool(updateHabitTool, "update_habit", "habit", "frequency_unit");
    }

    private static void AssertTool(Orbit.Application.Chat.Tools.IAiTool tool, string expectedName, string descriptionFragment, string schemaFragment, bool expectReadOnly = false)
    {
        tool.Name.Should().Be(expectedName);
        tool.Description.Should().NotBeNullOrWhiteSpace();
        tool.Description.ToLowerInvariant().Should().Contain(descriptionFragment.ToLowerInvariant(),
            $"tool '{expectedName}' description should mention '{descriptionFragment}'");
        tool.IsReadOnly.Should().Be(expectReadOnly);
        var schema = JsonSerializer.Serialize(tool.GetParameterSchema());
        schema.Should().Contain("\"type\"");
        schema.Should().Contain(schemaFragment,
            $"tool '{expectedName}' parameter schema should include '{schemaFragment}'");
    }

    private static IGenericRepository<T> Repo<T>()
        where T : Entity
    {
        return Substitute.For<IGenericRepository<T>>();
    }
}
