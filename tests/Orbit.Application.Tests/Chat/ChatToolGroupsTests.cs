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
    public void ResolveActiveToolNames_UnknownTool_TreatedAsCore()
    {
        var active = ChatToolGroups.ResolveActiveToolNames(["some_future_tool"], "hi");

        active.Should().Contain("some_future_tool");
    }
}
