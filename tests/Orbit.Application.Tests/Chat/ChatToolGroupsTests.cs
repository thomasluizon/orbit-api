using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public class ChatToolGroupsTests
{
    private static readonly string[] AllTools =
    [
        "create_habit", "log_habit", "query_habits", "create_goal", "get_gamification_overview",
        "get_calendar_overview", "manage_calendar_sync",
        "get_subscription_overview", "manage_subscription",
        "get_notifications", "update_notifications",
    ];

    [Fact]
    public void ResolveActiveToolNames_RareHabitOperations_AreGated()
    {
        string[] rare = ["reorder_habits", "move_habit_parent", "duplicate_habit", "create_sub_habit",
            "link_habits_to_goal", "get_daily_summary", "get_retrospective", "update_checklist"];
        var active = ChatToolGroups.ResolveActiveToolNames(AllTools.Concat(rare), "show my habits");

        active.Should().NotIntersectWith(rare);
    }

    [Theory]
    [InlineData("reorder my habits", "reorder_habits")]
    [InlineData("reorganizar meus hábitos", "reorder_habits")]
    [InlineData("duplicate my run", "duplicate_habit")]
    [InlineData("duplicar corrida", "duplicate_habit")]
    [InlineData("add a sub-habit", "create_sub_habit")]
    [InlineData("vincular hábito à meta", "link_habits_to_goal")]
    [InlineData("resumo do dia", "get_daily_summary")]
    [InlineData("show my retrospective", "get_retrospective")]
    [InlineData("editar checklist", "update_checklist")]
    public void ResolveActiveToolNames_RareHabitKeywords_ActivateTool(string message, string tool)
    {
        ChatToolGroups.ResolveActiveToolNames(AllTools.Append(tool), message).Should().Contain(tool);
    }

    [Fact]
    public void ResolveActiveToolNames_NoExtendedKeywords_KeepsCoreDropsExtended()
    {
        var active = ChatToolGroups.ResolveActiveToolNames(AllTools, "log my run and show my habits");

        active.Should().Contain("create_habit").And.Contain("log_habit").And.Contain("query_habits");
        active.Should().Contain("get_gamification_overview");
        active.Should().NotContain("get_calendar_overview");
        active.Should().NotContain("manage_subscription");
        active.Should().NotContain("get_notifications");
    }

    [Fact]
    public void ResolveActiveToolNames_DomainKeywordPresent_UnlocksThatGroupOnly()
    {
        var active = ChatToolGroups.ResolveActiveToolNames(AllTools, "sync my Google Calendar please");

        active.Should().Contain("get_calendar_overview").And.Contain("manage_calendar_sync");
        active.Should().NotContain("manage_subscription");
        active.Should().NotContain("get_notifications");
    }

    [Fact]
    public void ResolveActiveToolNames_PortugueseAccentedKeyword_Unlocks()
    {
        var active = ChatToolGroups.ResolveActiveToolNames(AllTools, "quero cancelar minha assinatura");

        active.Should().Contain("manage_subscription").And.Contain("get_subscription_overview");
    }

    [Fact]
    public void ResolveActiveToolNames_UnlockSurvivesLaterTurnsWithHistory()
    {
        string[] tools = ["get_profile", "duplicate_habit", "create_habit"];
        var first = ChatToolGroups.ResolveActiveToolNames(tools, "duplicar corrida");
        var fifth = ChatToolGroups.ResolveActiveToolNames(tools, "duplicar corrida\nokay\nthanks\nyes\ncontinue");
        fifth.Should().BeEquivalentTo(first).And.Contain("duplicate_habit");
    }

    [Fact]
    public void ResolveActiveToolNames_UnknownTool_TreatedAsCore()
    {
        var active = ChatToolGroups.ResolveActiveToolNames(["some_future_tool"], "hi");

        active.Should().Contain("some_future_tool");
    }

    [Theory]
    [InlineData("log my run and show my habits")]
    [InlineData("sync my Google Calendar please")]
    [InlineData("show my notification settings")]
    [InlineData("hello Astra")]
    public void ResolveActiveToolNames_UnrelatedTurn_PreservesExistingToolList(string message)
    {
        var existing = ChatToolGroups.ResolveActiveToolNames(AllTools, message);
        var withProactive = ChatToolGroups.ResolveActiveToolNames(
            AllTools.Append("set_proactive_astra"), message);

        withProactive.Should().BeEquivalentTo(existing);
    }

    [Theory]
    [InlineData("turn proactive Astra off")]
    [InlineData("enable proactive Astra")]
    [InlineData("desative a Astra proativa")]
    [InlineData("ativar Astra proativo")]
    public void ResolveActiveToolNames_ProactiveRequest_AddsOnlyProactiveTool(string message)
    {
        var active = ChatToolGroups.ResolveActiveToolNames(
            AllTools.Append("set_proactive_astra"), message);
        var existing = ChatToolGroups.ResolveActiveToolNames(AllTools, "log my run");

        active.Should().BeEquivalentTo(existing.Append("set_proactive_astra"));
    }
}
