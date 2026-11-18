#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Reflection;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.ChangeListProbe;

/// <summary>One provider catalog of a chat turn: the SDK tools that the real request sends, by name.</summary>
public sealed record ProbeCatalog(string Shape, string Client, IReadOnlyDictionary<string, FunctionTool> Tools)
{
    /// <summary>The serialized tool array of the request, as the SDK writes it.</summary>
    public string ToolsJson => "[" + string.Join(",", Tools.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => ModelReaderWriter.Write(pair.Value).ToString())) + "]";

    /// <summary>The wire parameters of each tool.</summary>
    public IEnumerable<(string Name, JsonElement Parameters)> Parameters() => Tools.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => (pair.Key, JsonDocument.Parse(ModelReaderWriter.Write(pair.Value).ToString()).RootElement.GetProperty("parameters").Clone()));
}

/// <summary>
/// Builds the real tool declarations of the first chat request through the turn handler and the SDK conversion of the API,
/// for a client catalog (<c>legacy</c>, <c>composition</c> or <c>offers</c>) and a declaration shape of the
/// <c>ai_change_plan</c> path. A null shape is the old path. No request leaves the process.
/// </summary>
public static class ChangeListCatalogs
{
    public static readonly string[] Clients = ["legacy", "composition", "offers"];

    public static async Task<ProbeCatalog> BuildAsync(ChangeDeclarationShape? shape, string client)
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
        var pending = Substitute.For<IPendingAgentOperationStore>();
        pending.GetAwaitingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingAgentOperationExecution>());
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(Arg.Any<Guid>(), Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var data = new ChatDataDependencies(Substitute.For<IGenericRepository<Habit>>(), Substitute.For<IGenericRepository<Goal>>(),
            Substitute.For<IGenericRepository<User>>(), Substitute.For<IGenericRepository<Tag>>(),
            Substitute.For<IGenericRepository<ChecklistTemplate>>(), Substitute.For<IFeatureFlagService>());
        var execution = new ChatExecutionDependencies(Substitute.For<IUserDateService>(), Substitute.For<IUserStreakService>(), quota,
            Substitute.For<IUnitOfWork>(), Substitute.For<IServiceScopeFactory>(), Substitute.For<IAgentOperationExecutor>(),
            Substitute.For<IPendingClarificationStore>(), Substitute.For<IGoalProgressReadSyncer>(),
            Substitute.For<IGamificationService>(), Substitute.For<IMediator>(), Substitute.For<IProductAnalytics>(),
            Substitute.For<IHabitScheduleLogReader>(), Substitute.For<IIssuedAiBlockStore>(), pending,
            Substitute.For<IPendingOperationChangePreviewer>());
        var handler = new ProcessUserChatCommandHandler(data, new(intent, registry, Substitute.For<ISystemPromptBuilder>(),
                Substitute.For<IAgentCatalogService>(), shape ?? ChangeDeclarationShape.Kinds), execution,
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(),
            Substitute.For<IContentModerationService>());
        var handlerType = typeof(ProcessUserChatCommandHandler);
        var todayFacts = Activator.CreateInstance(handlerType.Assembly.GetType("Orbit.Application.Chat.HabitTodaySnapshot", true)!,
            new object?[] { new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), null });
        string[] flags = shape is null ? [FeatureFlagKeys.AiBlockFirst] : [FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiChangePlan];
        var context = Activator.CreateInstance(handlerType.GetNestedType("ChatContext", BindingFlags.NonPublic)!,
            new object?[] { Array.Empty<Habit>(), new List<Habit>(), new List<Habit>(), false, null, null, true,
                Array.Empty<Goal>(), Array.Empty<Tag>(), Array.Empty<string>(), flags, DateOnly.MinValue, todayFacts, 0L });
        var offers = client == "offers";
        var request = new ProcessUserChatCommand(Guid.Empty, "", ClientContext: new AgentClientContext(
            SupportsParts: client != "legacy", PartKinds: ["choice", "pickList", "question"],
            SupportsPendingOperationChanges: offers, SupportsProactiveOffers: offers,
            ConversationId: offers ? Guid.Parse("11111111-1111-4111-8111-111111111111") : null));
        var state = Activator.CreateInstance(handlerType.GetNestedType("ChatTurnState", BindingFlags.NonPublic)!, nonPublic: true);
        await (Task<Result<AiResponse>>)handlerType.GetMethod("RequestInitialAiResponseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(handler, [request, context, null, new AiTurnTelemetry(), state, CancellationToken.None])!;
        var catalog = (IDictionary<string, ResponseTool>)typeof(AiIntentService)
            .GetMethod("BuildToolCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [captured!.ToolDeclarations])!;
        return new ProbeCatalog(shape?.ToString().ToLowerInvariant() ?? "old", client,
            catalog.ToDictionary(pair => pair.Key, pair => (FunctionTool)pair.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// The schema string size of the budget test: the length of each property name and each enum value, counted at each
    /// occurrence, without descriptions or required copies.
    /// </summary>
    public static int SchemaStringLength(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Array) return schema.EnumerateArray().Sum(SchemaStringLength);
        if (schema.ValueKind != JsonValueKind.Object) return 0;
        var size = 0;
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "properties") size += property.Value.EnumerateObject().Sum(field => field.Name.Length);
            if (property.Name == "enum") size += property.Value.EnumerateArray().Sum(value =>
                value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : value.GetRawText().Length);
            size += SchemaStringLength(property.Value);
        }
        return size;
    }

    /// <summary>The object depth of a schema with each reference resolved; a reference that recurs counts once.</summary>
    public static int ObjectDepth(JsonElement schema) => ObjectDepth(schema, schema, []);

    private static int ObjectDepth(JsonElement schema, JsonElement root, HashSet<string> active)
    {
        var reference = schema.TryGetProperty("$ref", out var target) ? target.GetString() : null;
        if (reference is not null && !active.Add(reference)) return 0;
        schema = ToolSchemaReferences.Resolve(schema, root);
        var own = schema.TryGetProperty("properties", out var properties) ? 1 : 0;
        var children = new List<int> { 0 };
        if (own != 0) children.AddRange(properties.EnumerateObject().Select(property => ObjectDepth(property.Value, root, active)));
        if (schema.TryGetProperty("items", out var items)) children.Add(ObjectDepth(items, root, active));
        if (schema.TryGetProperty("anyOf", out var alternatives))
            children.AddRange(alternatives.EnumerateArray().Select(alternative => ObjectDepth(alternative, root, active)));
        if (reference is not null) active.Remove(reference);
        return own + children.Max();
    }

    /// <summary>The number of properties named <paramref name="name"/> in a schema, at any depth.</summary>
    public static int CountProperty(JsonElement schema, string name) => schema.ValueKind switch
    {
        JsonValueKind.Object => schema.EnumerateObject().Sum(property =>
            (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty(name, out _) ? 1 : 0) + CountProperty(property.Value, name)),
        JsonValueKind.Array => schema.EnumerateArray().Sum(item => CountProperty(item, name)),
        _ => 0
    };

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
