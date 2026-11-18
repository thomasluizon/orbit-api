using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using ModelContextProtocol.Server;
using Orbit.Api.Controllers;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

/// <summary>
/// The closed list of the routes that reach the executor, the previewer, the revision service or the conversation
/// store. A route with the label <see cref="Part2"/> writes the same-title detail on its open previews. A route with
/// the label <see cref="NoItems"/> returns no preview item.
/// </summary>
public sealed class PreviewItemRouteLabelTests
{
    private const string Part2 = "Part 2";
    private const string NoItems = "no items";

    private static readonly Dictionary<string, string> ActionLabels = new()
    {
        ["ChatController.ProcessChat"] = Part2,
        ["ChatController.ProcessChatStream"] = Part2,
        ["ChatController.PreviewHabitSuggestion"] = Part2,
        ["ChatController.PreviewAllHabitSuggestions"] = Part2,
        ["ChatController.GetLatestConversation"] = Part2,
        ["ChatController.RefreshHabitMonthReadBlock"] = NoItems,
        ["ChatController.RefreshReadBlock"] = NoItems,
        ["ChatController.GetRecordListPage"] = NoItems,
        ["ChatController.Transcribe"] = NoItems,
        ["ChatController.GetConversationImage"] = NoItems,
        ["ChatController.DeleteConversation"] = NoItems,
        ["ChatController.RefreshGamificationReadBlock"] = NoItems,
        ["ChatController.RefineHabitSuggestion"] = NoItems,
        ["ChatController.QueryHabitRanking"] = NoItems,
        ["ChatController.GetHabitRankingPage"] = NoItems,
        ["ChatController.DismissOffers"] = NoItems,
        ["AiController.RefreshPendingOperationPreview"] = Part2,
        ["AiController.RevisePendingOperation"] = Part2,
        ["AiController.ResolveClarification"] = Part2,
        ["AiController.ExecutePendingOperation"] = Part2,
        ["AiController.VerifyPendingOperationStepUp"] = Part2,
        ["AiController.GetCapabilitiesMetadata"] = NoItems,
        ["AiController.GetOperationsMetadata"] = NoItems,
        ["AiController.GetUserDataCatalog"] = NoItems,
        ["AiController.GetAppSurfaces"] = NoItems,
        ["AiController.ConfirmPendingOperation"] = NoItems,
        ["AiController.MarkPendingOperationStepUp"] = NoItems,
        ["ProactiveAiController.Current"] = NoItems
    };

    /// <summary>
    /// The conversation page returns the stored response as a JSON string, so its declared type holds no item type.
    /// The list names it by hand.
    /// </summary>
    private static readonly string[] StoredJsonRoutes = ["ChatController.GetLatestConversation"];

    /// <summary>
    /// The MCP tool classes that hold the executor or its bridge. Each tool of a listed class has the label
    /// <see cref="NoItems"/>: the executor adds preview items only on the chat surface.
    /// </summary>
    private static readonly string[] McpToolClasses =
    [
        nameof(AccountTools), nameof(AgentTools), nameof(ApiKeyTools), nameof(CalendarTools), nameof(ChecklistTemplateTools),
        nameof(FeatureFlagTools), nameof(GoalTools), nameof(HabitTools), nameof(NotificationTools),
        nameof(ProfileTools), nameof(RecordPageTools), nameof(SubscriptionTools), nameof(SupportTools), nameof(TagTools)
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller) => controller
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string Name(MethodInfo action) => $"{action.DeclaringType!.Name}.{action.Name}";

    private static IEnumerable<Type> DeclaredResponseTypes(MethodInfo action) => action
        .GetCustomAttributes<ProducesResponseTypeAttribute>()
        .Select(attribute => attribute.Type).Where(type => type != typeof(void));

    [Fact]
    public void EachChatAndAiAction_HasOneLabel()
    {
        var actions = Actions(typeof(ChatController)).Concat(Actions(typeof(AiController))).Select(Name)
            .Append(Name(typeof(ProactiveAiController).GetMethod(nameof(ProactiveAiController.Current))!)).ToArray();

        actions.Should().OnlyHaveUniqueItems();
        actions.Should().BeEquivalentTo(ActionLabels.Keys,
            "each action of ChatController and AiController needs the label Part 2 or no items in this list");
        ActionLabels.Values.Should().OnlyContain(label => label == Part2 || label == NoItems);
    }

    [Fact]
    public void EachAiAction_DeclaresItsResponseType()
    {
        foreach (var action in Actions(typeof(AiController)))
            DeclaredResponseTypes(action).Should().NotBeEmpty($"{Name(action)} needs a declared response type, so this list can read it");
    }

    [Fact]
    public void ActionWithTheLabelNoItems_DeclaresNoResponseTypeThatHoldsAPreviewItem()
    {
        var actions = Actions(typeof(ChatController)).Concat(Actions(typeof(AiController)))
            .Concat(Actions(typeof(ProactiveAiController))).ToDictionary(Name);

        foreach (var (name, label) in ActionLabels)
        {
            var holds = DeclaredResponseTypes(actions[name]).Any(HoldsPreviewItem);
            if (label == NoItems)
                holds.Should().BeFalse($"{name} has the label no items, but its declared response type holds a PendingOperationItem");
            else if (!StoredJsonRoutes.Contains(name))
                holds.Should().BeTrue($"{name} has the label Part 2, so its declared response type holds a PendingOperationItem");
        }
    }

    [Fact]
    public void EachMcpToolThatReachesTheExecutor_IsInAListedClassAndReturnsNoPreviewItem()
    {
        var classes = typeof(ChatController).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
                && type.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                    .Any(parameter => parameter.ParameterType == typeof(McpExecutorBridge)
                        || parameter.ParameterType == typeof(IAgentOperationExecutor)))
            .ToArray();

        classes.Select(type => type.Name).Should().BeEquivalentTo(McpToolClasses,
            "an MCP tool class that holds the executor or its bridge needs the label no items in this list");
        var tools = classes.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly)).Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null).ToArray();
        tools.Should().NotBeEmpty();
        foreach (var tool in tools)
            HoldsPreviewItem(tool.ReturnType).Should().BeFalse($"{Name(tool)} is an MCP tool, and an MCP tool returns no preview item");
        HoldsPreviewItem(typeof(McpExecutorResult)).Should().BeFalse();
    }

    [Fact]
    public void TypeWalk_FindsAPreviewItemThroughPropertiesAndGenericArguments()
    {
        HoldsPreviewItem(typeof(PendingOperationItem)).Should().BeTrue();
        HoldsPreviewItem(typeof(Task<IReadOnlyList<PendingAgentOperation>>)).Should().BeTrue();
        HoldsPreviewItem(typeof(AgentExecuteOperationResponse)).Should().BeTrue();
        HoldsPreviewItem(typeof(AgentStepUpChallenge)).Should().BeFalse();
        HoldsPreviewItem(typeof(AiController.ConfirmPendingOperationResponse)).Should().BeFalse();
    }

    private static bool HoldsPreviewItem(Type type) => HoldsPreviewItem(type, []);

    private static bool HoldsPreviewItem(Type type, HashSet<Type> seen)
    {
        if (type == typeof(PendingOperationItem)) return true;
        if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(object)) return false;
        if (type.HasElementType) return HoldsPreviewItem(type.GetElementType()!, seen);
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => HoldsPreviewItem(argument, seen))) return true;
        if (type.Namespace?.StartsWith("Orbit.", StringComparison.Ordinal) != true) return false;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => HoldsPreviewItem(property.PropertyType, seen));
    }
}
