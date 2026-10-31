#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public sealed class ProviderToolControlOrderTests
{
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
    {
        "orbit_read_complete", "orbit_write_complete", "orbit_dismissed_offer_keys",
        "suggested_fields", "orbit_offer_metadata"
    };

    [Theory]
    [InlineData("legacy", "initial")]
    [InlineData("legacy", "post-read")]
    [InlineData("legacy", "draft-revision")]
    [InlineData("composition", "initial")]
    [InlineData("composition", "post-read")]
    [InlineData("composition", "draft-revision")]
    [InlineData("offers", "initial")]
    [InlineData("offers", "post-read")]
    [InlineData("offers", "draft-revision")]
    public async Task AllProviderDeclarations_PutControlsBeforePayloads(string client, string phase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        foreach (var name in new[] { "AddAiPlatformServices", "AddAiChatTools" })
            typeof(Orbit.Api.Extensions.ServiceCollectionExtensions).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [builder]);
        var construction = new ToolConstruction();
        var registry = new AiToolRegistry(builder.Services.Where(service => service.ServiceType == typeof(IAiTool))
            .Select(service => (IAiTool)(service.ImplementationType is { } type
                ? construction.GetService(type) : service.ImplementationFactory!(construction))!));
        var intent = Substitute.For<IAiIntentService>();
        AiToolRequest? captured = null;
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<AiToolRequest>();
                return Result.Success(new AiResponse());
            });
        var prompts = Substitute.For<ISystemPromptBuilder>();
        var catalog = Substitute.For<IAgentCatalogService>();
        var pending = Substitute.For<IPendingAgentOperationStore>();
        pending.GetAwaiting(Arg.Any<Guid>()).Returns(Array.Empty<PendingAgentOperationExecution>());
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(Arg.Any<Guid>(), Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var execution = new ChatExecutionDependencies(Substitute.For<IUserDateService>(), null!, quota,
            Substitute.For<IUnitOfWork>(), null!, null!, null!, null!, null!, null!, null!, null!, null!, pending,
            Substitute.For<IPendingOperationChangePreviewer>());
        var handler = new ProcessUserChatCommandHandler(null!, new(intent, registry, prompts, catalog), execution,
            NullLogger<ProcessUserChatCommandHandler>.Instance, null!, null!);
        var handlerType = typeof(ProcessUserChatCommandHandler);
        var todayFacts = Activator.CreateInstance(handlerType.Assembly.GetType("Orbit.Application.Chat.HabitTodaySnapshot", true)!,
            new object?[] { new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), null });
        var context = Activator.CreateInstance(handlerType.GetNestedType("ChatContext", BindingFlags.NonPublic)!,
            new object?[] { Array.Empty<Habit>(), new List<Habit>(), new List<Habit>(), false, null, null, true,
                Array.Empty<Goal>(), Array.Empty<Tag>(), Array.Empty<string>(), new[] { FeatureFlagKeys.AstraBlockFirst },
                DateOnly.MinValue, todayFacts, 0L });
        var offers = client == "offers";
        var request = new ProcessUserChatCommand(Guid.Empty, "", ClientContext: new AgentClientContext(
            SupportsParts: client != "legacy", PartKinds: ["choice", "pickList", "question"],
            SupportsPendingOperationChanges: offers, SupportsProactiveOffers: offers,
            ConversationId: offers ? Guid.Parse("11111111-1111-4111-8111-111111111111") : null));
        var state = Activator.CreateInstance(handlerType.GetNestedType("ChatTurnState", BindingFlags.NonPublic)!, nonPublic: true);
        await (Task<Result<AiResponse>>)handlerType.GetMethod("RequestInitialAiResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(handler, [request, context, null, new AiTurnTelemetry(), state, CancellationToken.None])!;
        captured.Should().NotBeNull();
        var declarations = captured!.ToolDeclarations.ToList();
        var draft = new HabitSetupDraft("draft", "Activity", "frequency_unit", Guid.Empty);
        if (phase != "initial")
        {
            var resultsType = handlerType.GetNestedType("ToolExecutionAccumulator", BindingFlags.NonPublic)!;
            var results = Activator.CreateInstance(resultsType, nonPublic: true)!;
            resultsType.GetProperty("HasSuccessfulAccountRead")!.SetValue(results, phase == "post-read");
            if (phase == "draft-revision")
            {
                ((IDictionary<string, HabitSetupDraft>)resultsType.GetProperty("MissingHabitDrafts")!.GetValue(results)!).Add(draft.CallId, draft);
            }
            var conversation = new AiConversationContext();
            handlerType.GetMethod("RefreshAskSchema", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(handler, [conversation, request, context, results]);
            conversation.ToolDeclarationUpdates.Should().HaveCount(client == "legacy" ? 0 : 1);
            declarations.AddRange(conversation.ToolDeclarationUpdates);
        }
        var sdkCatalog = (IDictionary<string, ResponseTool>)typeof(AiIntentService)
            .GetMethod("BuildToolCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [declarations])!;
        sdkCatalog.Should().NotBeEmpty();
        var violations = new List<string>();
        foreach (var (name, tool) in sdkCatalog)
        {
            using var wire = JsonDocument.Parse(ModelReaderWriter.Write(tool).ToString());
            var parameters = wire.RootElement.GetProperty("parameters");
            AssertControlOrder(parameters, name, violations);
            var source = registry.GetTool(name) is AskTool ask
                ? ask.GetParameterSchema(phase == "post-read", phase == "draft-revision" ? [draft] : null)
                : registry.GetTool(name)!.GetParameterSchema();
            AssertPayloadOrder(JsonSerializer.SerializeToElement(source), parameters, name);
        }
        violations.Should().BeEmpty("strict generation emits properties in declaration order; controls after a large payload caused whitespace runaway");
    }

    private static void AssertControlOrder(JsonElement schema, string path, List<string> violations)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            Check(properties.EnumerateObject().Select(property => property.Name), path + ".properties");
            Check(schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!), path + ".required");
            foreach (var property in properties.EnumerateObject()) AssertControlOrder(property.Value, path + "." + property.Name, violations);
        }
        if (schema.TryGetProperty("items", out var items)) AssertControlOrder(items, path + "[]", violations);
        if (schema.TryGetProperty("anyOf", out var alternatives))
            foreach (var alternative in alternatives.EnumerateArray()) AssertControlOrder(alternative, path + ".anyOf", violations);
        if (schema.TryGetProperty("$defs", out var definitions))
            foreach (var definition in definitions.EnumerateObject()) AssertControlOrder(definition.Value, path + ".$defs." + definition.Name, violations);

        void Check(IEnumerable<string> names, string location)
        {
            var payloadSeen = false;
            foreach (var name in names)
                if (!Controls.Contains(name)) payloadSeen = true;
                else if (payloadSeen) violations.Add(location + ": " + name + " follows a payload field");
        }
    }

    private static void AssertPayloadOrder(JsonElement source, JsonElement wire, string path)
    {
        if (source.TryGetProperty("properties", out var properties))
        {
            var wireProperties = wire.GetProperty("properties");
            var expected = properties.EnumerateObject().Where(property => !Controls.Contains(property.Name)
                && wireProperties.TryGetProperty(property.Name, out _)).Select(property => property.Name);
            wireProperties.EnumerateObject().Select(property => property.Name).Where(name => !Controls.Contains(name))
                .Should().Equal(expected, path);
            foreach (var property in properties.EnumerateObject())
                if (wireProperties.TryGetProperty(property.Name, out var value)) AssertPayloadOrder(property.Value, value, path + "." + property.Name);
        }
        if (source.TryGetProperty("items", out var items)) AssertPayloadOrder(items, wire.GetProperty("items"), path + "[]");
        if (source.TryGetProperty("anyOf", out var alternatives))
            foreach (var pair in alternatives.EnumerateArray().Zip(wire.GetProperty("anyOf").EnumerateArray()))
                AssertPayloadOrder(pair.First, pair.Second, path + ".anyOf");
    }

    private sealed class ToolConstruction : IServiceProvider
    {
        public object GetService(Type type)
        {
            var constructor = type.GetConstructors().Single();
            return constructor.Invoke(constructor.GetParameters().Select(parameter =>
                typeof(IAiTool).IsAssignableFrom(parameter.ParameterType) ? GetService(parameter.ParameterType) : null).ToArray());
        }
    }
}
