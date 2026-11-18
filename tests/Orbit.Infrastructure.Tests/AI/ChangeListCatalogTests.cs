using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.ChangeListProbe;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.AI;

/// <summary>
/// The offline schema checks of the typed change list (thomasluizon/orbit-tickets#2160) for each declaration shape and
/// each client catalog, on the real declarations that the provider probe sends. They do not prove provider acceptance;
/// only the probe does.
/// </summary>
public sealed class ChangeListCatalogTests(ITestOutputHelper output)
{
    private const int SchemaStringBudget = 21_000;
    private const int DepthLimit = 10;

    /// <summary>The change kinds of the chat registry, as a closed list. A new write tool fails this test until it is listed.</summary>
    private static readonly string[] Kinds =
    [
        "assign_tags", "bulk_create_habits", "bulk_delete_habits", "bulk_log_habits", "bulk_reschedule_habits",
        "bulk_skip_habits", "bulk_update_habit_emojis", "bulk_update_habits", "create_checklist_template", "create_goal",
        "create_tag", "delete_checklist_template", "delete_goal", "delete_notifications", "delete_tag", "duplicate_habit",
        "get_referral_code", "link_goals_to_habit", "link_habits_to_goal", "manage_account", "manage_api_keys",
        "manage_calendar_sync", "manage_subscription", "move_habit", "move_habit_parent", "reorder_goals", "reorder_habits",
        "send_support_request", "set_ai_summary", "set_proactive_astra", "undo_skip_habit", "unlog_habit",
        "update_checklist", "update_checklist_template", "update_goal", "update_goal_progress", "update_goal_status",
        "update_habit", "update_notifications", "update_profile_preferences", "update_tag"
    ];

    private static readonly string[] MergedForms = ["create_habit", "create_sub_habit", "delete_habit", "log_habit", "skip_habit"];

    public static TheoryData<ChangeDeclarationShape, string> Catalogs()
    {
        var data = new TheoryData<ChangeDeclarationShape, string>();
        foreach (var shape in new[] { ChangeDeclarationShape.Wrapper, ChangeDeclarationShape.Kinds })
            foreach (var client in ChangeListCatalogs.Clients)
                data.Add(shape, client);
        return data;
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task EachShapeAndCatalog_StaysWithinTheDepthLimitAndTheSchemaStringBudget(ChangeDeclarationShape shape, string client)
    {
        var catalog = await ChangeListCatalogs.BuildAsync(shape, client);
        var parameters = catalog.Parameters().ToArray();
        var size = parameters.Sum(tool => ChangeListCatalogs.SchemaStringLength(tool.Parameters));
        var depth = parameters.Max(tool => ChangeListCatalogs.ObjectDepth(tool.Parameters));
        var completion = parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, TerminalWriteDirective.CompleteArgument));
        var suggested = parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, ProactiveProposal.Argument));
        var dismissal = parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, "orbit_dismissed_offer_keys"));
        output.WriteLine($"{catalog.Shape}/{client}: tools={catalog.Tools.Count} tokens={O200kTokenCounter.Shared.CountTokens(catalog.ToolsJson)} "
            + $"S={size} depth={depth} orbit_write_complete={completion} suggested_fields={suggested} orbit_dismissed_offer_keys={dismissal}");

        size.Should().BeLessThanOrEqualTo(SchemaStringBudget);
        foreach (var (name, schema) in parameters)
        {
            ChangeListCatalogs.ObjectDepth(schema).Should().BeLessThanOrEqualTo(DepthLimit, name);
            StrictChatToolSchemaTests.AssertStrict(schema);
            StrictChatToolSchemaTests.AssertNoEnumWithPattern(schema, catalog.Shape + "/" + client + "/" + name);
        }
        var names = catalog.Tools.Keys.ToArray();
        names.Should().NotContain(PlanToolDeclaration.Name).And.NotContain(MergedForms);
        if (shape == ChangeDeclarationShape.Wrapper)
        {
            names.Should().Contain(ChangeDeclarations.WrapperName).And.NotContain(Kinds);
            completion.Should().Be(1, "the wrapper holds the completion argument once");
            dismissal.Should().Be(client == "offers" ? names.Length : 0);
        }
        else
        {
            names.Should().Contain(Kinds).And.NotContain(ChangeDeclarations.WrapperName);
            completion.Should().Be(Kinds.Length);
        }
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("composition")]
    [InlineData("offers")]
    public async Task TheOldPath_StillDeclaresPresentPlanAndEachWriteTool(string client)
    {
        var catalog = await ChangeListCatalogs.BuildAsync(null, client);
        var parameters = catalog.Parameters().ToArray();
        output.WriteLine($"old/{client}: tools={catalog.Tools.Count} tokens={O200kTokenCounter.Shared.CountTokens(catalog.ToolsJson)} "
            + $"S={parameters.Sum(tool => ChangeListCatalogs.SchemaStringLength(tool.Parameters))} "
            + $"depth={parameters.Max(tool => ChangeListCatalogs.ObjectDepth(tool.Parameters))} "
            + $"orbit_write_complete={parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, TerminalWriteDirective.CompleteArgument))} "
            + $"suggested_fields={parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, ProactiveProposal.Argument))} "
            + $"orbit_dismissed_offer_keys={parameters.Sum(tool => ChangeListCatalogs.CountProperty(tool.Parameters, "orbit_dismissed_offer_keys"))}");

        catalog.Tools.Keys.Should().Contain(PlanToolDeclaration.Name).And.Contain(Kinds).And.Contain(MergedForms)
            .And.NotContain(ChangeDeclarations.WrapperName);
    }

    [Fact]
    public void TheChangeKinds_AreAClosedList() =>
        ChangeKinds.All(new AiToolRegistry(StrictChatToolSchemaTests.AllTools())).Select(tool => tool.Name).Should().Equal(Kinds);

    public static TheoryData<string> KindNames() => [.. Kinds];

    /// <summary>
    /// Each change kind of the real registry runs its own write tool call with its own arguments, and its preview is the
    /// preview that the executor gives for that call.
    /// </summary>
    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task EachChangeKind_CompilesThroughItsWriteToolToItsPreview(string kind)
    {
        var registry = new AiToolRegistry(StrictChatToolSchemaTests.AllTools());
        var store = Substitute.For<IPendingAgentOperationStore>();
        var arguments = JsonSerializer.SerializeToElement(new { probe = kind });
        var preview = new PendingAgentOperation(Guid.NewGuid(), kind, kind, kind, AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10));
        AiToolCall? dispatched = null;

        var outcome = await new ChangeListCompiler(registry).CompileAsync(Guid.NewGuid(), [new ProposedChange("change", kind, arguments)],
            new HashSet<Guid>(), (calls, _) =>
            {
                dispatched = calls.Should().ContainSingle().Subject;
                return Task.FromResult<IReadOnlyList<ChangeResult>>([new ChangeResult(dispatched.Id, preview, null)]);
            }, store, default);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Previews.Should().Equal(preview);
        dispatched.Should().NotBeNull();
        dispatched!.Name.Should().Be(kind);
        dispatched.Args.GetRawText().Should().Be(arguments.GetRawText());
    }
}
