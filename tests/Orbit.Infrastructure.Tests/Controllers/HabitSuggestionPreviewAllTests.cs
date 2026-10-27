using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class HabitSuggestionPreviewAllTests : IAsyncDisposable
{
    private readonly SqliteOrbitDbContextFactory _database = new();
    private readonly WebApplication _app;
    private readonly RequestDelegate _pipeline;
    private readonly Guid _userId;
    private readonly Habit _existing;
    private readonly DateOnly _today = new(2026, 10, 26);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public HabitSuggestionPreviewAllTests()
    {
        var user = User.Create("Alex", "suggestions@example.test").Value;
        _userId = user.Id;
        _existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read",
            FrequencyUnit.Day, 1, _today, DueTime: new(22, 0), DueEndTime: new(22, 15))).Value;
        _database.Context.Users.Add(user);
        _database.Context.Habits.Add(_existing);
        _database.Context.SaveChanges();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        builder.Services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
        builder.Services.AddAuthorization();
        var auth = Substitute.For<IAuthenticationService>();
        auth.ForbidAsync(Arg.Any<HttpContext>(), Arg.Any<string?>(), Arg.Any<AuthenticationProperties?>())
            .Returns(call => { call.Arg<HttpContext>().Response.StatusCode = 403; return Task.CompletedTask; });
        builder.Services.AddSingleton(auth);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_today);
        dates.GetUserWeekStartDayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        var gate = Substitute.For<IPayGateService>();
        gate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        var limiter = Substitute.For<IDistributedRateLimitService>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        builder.Services.AddLogging().AddMemoryCache();
        builder.Services.AddSingleton(_database.Context);
        builder.Services.AddSingleton(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        builder.Services.AddSingleton<IUnitOfWork>(new UnitOfWork(_database.Context, new DatabaseConnectionSettings()));
        builder.Services.AddSingleton(dates).AddSingleton(gate).AddSingleton(config).AddSingleton(limiter);
        builder.Services.AddSingleton(Substitute.For<IAuthSessionService>());
        builder.Services.AddSingleton<Orbit.Api.RateLimiting.BffClientIpResolver>();
        builder.Services.AddSingleton(Substitute.For<IImageValidationService>());
        builder.Services.AddSingleton(Substitute.For<IGamificationService>());
        builder.Services.AddSingleton(Substitute.For<IGoalCompletionService>());
        builder.Services.AddSingleton(Substitute.For<IHabitDraftGenerator>());
        builder.Services.AddSingleton(Substitute.For<IAgentAuditService>());
        builder.Services.AddSingleton(Substitute.For<IAgentStepUpAuthorizationBridge>());
        builder.Services.AddSingleton(Substitute.For<IAgentStepUpService>());
        builder.Services.AddSingleton(Substitute.For<IPendingClarificationStore>());
        builder.Services.Configure<AgentPlatformSettings>(_ => { });
        builder.Services.AddMediatR(options =>
        {
            options.RegisterServicesFromAssemblyContaining<PreviewHabitSuggestionCommand>();
            options.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        builder.Services.AddValidatorsFromAssemblyContaining<PreviewHabitSuggestionCommand>();
        builder.Services.AddSingleton<CreateHabitRepositories>();
        builder.Services.AddSingleton<BulkCreateHabitsRepositories>();
        builder.Services.AddSingleton<HabitSuggestionService>();
        builder.Services.AddSingleton<AccountChangeSetService>();
        builder.Services.AddSingleton<IAiTool, CreateHabitTool>();
        builder.Services.AddSingleton<IAiTool, UpdateHabitTool>();
        builder.Services.AddSingleton<IAiTool, BulkCreateHabitsTool>();
        builder.Services.AddSingleton<AiToolRegistry>();
        builder.Services.AddSingleton<IAgentCatalogService, AgentCatalogService>();
        builder.Services.AddSingleton<IPendingAgentOperationStore, PendingAgentOperationStore>();
        builder.Services.AddSingleton<IPendingOperationChangePreviewer, PendingOperationChangePreviewer>();
        builder.Services.AddSingleton<IAgentTargetOwnershipService, AgentTargetOwnershipService>();
        builder.Services.AddSingleton<IFeatureFlagService, FeatureFlagService>();
        builder.Services.AddSingleton<IAgentPolicyEvaluator, AgentPolicyEvaluator>();
        builder.Services.AddSingleton<IAgentOperationExecutor, AgentOperationExecutor>();
        builder.Services.AddSingleton<PendingOperationRevisionService>();
        builder.Services.AddSingleton<AgentPendingStores>();
        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.UseRouting();
        _app.UseAuthorization();
        _app.UseEndpoints(endpoints => endpoints.MapControllers());
        _pipeline = ((IApplicationBuilder)_app).Build();
    }

    private HabitSuggestionDraft Draft(string title = "Stretch", int hour = 20) => new(Guid.NewGuid(), "📖", title,
        "Habit", FrequencyUnit.Day, 1, [], _today, new(hour, 0), new(hour, 15), null, [], "A quiet pause.");

    private async Task<(int Status, JsonElement Body)> Post(string path, object body, bool readOnly = false, string? scope = null)
    {
        await using var requestScope = _app.Services.CreateAsyncScope();
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, _userId.ToString())];
        if (readOnly || scope is not null)
        {
            claims.Add(new("auth_method", "api_key"));
            claims.Add(new("api_key_read_only", readOnly.ToString()));
            foreach (var granted in scope?.Split(' ') ?? [AgentScopes.ChatInteract, AgentScopes.ReadHabits, AgentScopes.ReadGoals, AgentScopes.WriteHabits])
                claims.Add(new("scope", granted));
        }
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new("api.useorbit.org");
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        await _pipeline(context);
        var response = ((MemoryStream)context.Response.Body).ToArray();
        return (context.Response.StatusCode, response.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(response));
    }

    [Fact]
    public async Task SinglePreview_ExistingEndpointStillStagesWithoutWriting()
    {
        var response = await Post("/api/chat/habit-suggestions/preview", new { draft = Draft(), language = "en" });
        response.Status.Should().Be(200, response.Body.ToString());
        response.Body.GetProperty("pendingOperation").GetProperty("items").GetArrayLength().Should().Be(1);
        _database.Context.Habits.Should().ContainSingle();
    }

    [Fact]
    public async Task PreviewAll_TwoCreatesAndAdjustment_OneApprovalExecutesEveryItem()
    {
        var drafts = new[] { Draft(), Draft("Walk", 21), Draft("Read", 23) with { ExistingHabitId = _existing.Id } };
        var response = await Post("/api/chat/habit-suggestions/preview-all", new { drafts, language = "en" });
        response.Status.Should().Be(200, response.Body.ToString());
        var preview = JsonSerializer.Deserialize<AgentExecuteOperationResponse>(response.Body, Json)!;
        preview.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        preview.Operation.OperationId.Should().Be(AccountChangeSet.OperationName);
        preview.PendingOperation!.Items.Should().HaveCount(3);
        preview.PendingOperation.Items!.Select(item => item.EntityName).Should().Equal(drafts.Select(draft => draft.Title));
        preview.PendingOperation.Items![2].EntityId.Should().Be(_existing.Id);
        var store = _app.Services.GetRequiredService<IPendingAgentOperationStore>();
        store.GetAwaiting(_userId).Should().ContainSingle().Which.PendingOperationId.Should().Be(preview.PendingOperation.Id);
        using (var before = _database.CreateContext())
        {
            before.Habits.Should().ContainSingle().Which.DueTime.Should().Be(new TimeOnly(22, 0));
        }
        var id = preview.PendingOperation.Id;
        var confirmed = await Post($"/api/ai/pending-operations/{id}/confirm", new { });
        confirmed.Status.Should().Be(200, confirmed.Body.ToString());
        var approved = await Post($"/api/ai/pending-operations/{id}/execute", new
        { confirmationToken = confirmed.Body.GetProperty("confirmationToken").GetString() });
        approved.Status.Should().Be(200, approved.Body.ToString());
        var result = JsonSerializer.Deserialize<AgentExecuteOperationResponse>(approved.Body, Json)!;
        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, approved.Body.ToString());
        using var after = _database.CreateContext();
        after.Habits.Select(habit => habit.Title).Should().BeEquivalentTo(drafts.Select(draft => draft.Title));
        after.Habits.Single(habit => habit.Id == _existing.Id).DueTime.Should().Be(new TimeOnly(23, 0));
        after.PendingAgentOperations.Should().OnlyContain(pending => pending.ConsumedAtUtc != null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAll_OneDraft_ReturnsTheSingleEndpointPreview(bool adjustment)
    {
        var draft = adjustment ? Draft("Read", 23) with { ExistingHabitId = _existing.Id } : Draft();
        var single = await Post("/api/chat/habit-suggestions/preview", new { draft, language = "en" });
        var batch = await Post("/api/chat/habit-suggestions/preview-all", new { drafts = new[] { draft }, language = "en" });
        batch.Status.Should().Be(single.Status).And.Be(200);
        batch.Body.GetRawText().Should().Be(single.Body.GetRawText());
        var preview = JsonSerializer.Deserialize<AgentExecuteOperationResponse>(batch.Body, Json)!;
        var id = preview.PendingOperation!.Id;
        var confirmed = await Post($"/api/ai/pending-operations/{id}/confirm", new { });
        confirmed.Status.Should().Be(200, confirmed.Body.ToString());
        var approved = await Post($"/api/ai/pending-operations/{id}/execute", new
        { confirmationToken = confirmed.Body.GetProperty("confirmationToken").GetString() });
        approved.Status.Should().Be(200, approved.Body.ToString());
        JsonSerializer.Deserialize<AgentExecuteOperationResponse>(approved.Body, Json)!.Operation.Status
            .Should().Be(AgentOperationStatus.Succeeded, approved.Body.ToString());
        using var after = _database.CreateContext();
        after.Habits.Should().HaveCount(adjustment ? 1 : 2);
        var saved = after.Habits.Single(habit => habit.Title == draft.Title);
        saved.DueTime.Should().Be(draft.DueTime);
        saved.DueEndTime.Should().Be(draft.DueEndTime);
        if (adjustment) saved.Id.Should().Be(_existing.Id);
    }

    [Fact]
    public async Task PreviewAll_RepeatedAdjustmentTarget_ReturnsIndexedErrorAndStagesNothing()
    {
        var drafts = new[]
        {
            Draft("Read", 20) with { ExistingHabitId = _existing.Id },
            Draft("Reading", 21) with { ExistingHabitId = _existing.Id }
        };
        var response = await Post("/api/chat/habit-suggestions/preview-all", new { drafts, language = "en" });
        AssertRejectedBatch(response, 1);
    }

    [Fact]
    public async Task PreviewAll_OverlappingNewDrafts_ReturnsIndexedErrorAndStagesNothing()
    {
        var response = await Post("/api/chat/habit-suggestions/preview-all", new
        { drafts = new[] { Draft(), Draft("Walk", 20) }, language = "en" });
        AssertRejectedBatch(response, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAll_DuplicateIdentityOrNormalizedName_ReturnsIndexedErrorAndStagesNothing(bool duplicateId)
    {
        var first = Draft();
        var duplicate = duplicateId ? Draft("Walk", 21) with { Id = first.Id } : Draft("STRETCH", 21);
        var response = await Post("/api/chat/habit-suggestions/preview-all", new
        { drafts = new[] { first, Draft("Pause", 19), duplicate }, language = "en" });
        AssertRejectedBatch(response, 2);
    }

    private void AssertRejectedBatch((int Status, JsonElement Body) response, int draftIndex)
    {
        response.Status.Should().Be(400, response.Body.ToString());
        response.Body.GetProperty("errorCode").GetString().Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        response.Body.GetProperty("draftIndex").GetInt32().Should().Be(draftIndex);
        _app.Services.GetRequiredService<IPendingAgentOperationStore>().GetAwaiting(_userId).Should().BeEmpty();
        using var after = _database.CreateContext();
        after.PendingAgentOperations.Should().BeEmpty();
        after.Habits.Should().ContainSingle().Which.DueTime.Should().Be(new TimeOnly(22, 0));
    }

    [Fact]
    public async Task PreviewAll_MaximumDrafts_AreAllIncluded()
    {
        var drafts = Enumerable.Range(0, HabitSuggestionsCard.MaxDrafts).Select(index => Draft($"Habit {index}", 10 + index)).ToArray();
        var response = await Post("/api/chat/habit-suggestions/preview-all", new { drafts });
        response.Status.Should().Be(200, response.Body.ToString());
        response.Body.GetProperty("pendingOperation").GetProperty("items").GetArrayLength().Should().Be(HabitSuggestionsCard.MaxDrafts);
    }

    [Fact]
    public async Task PreviewAll_FailingLaterDraft_ReturnsTypedIndexAndStagesNothing()
    {
        var response = await Post("/api/chat/habit-suggestions/preview-all", new
        { drafts = new[] { Draft(), Draft("Missing", 21) with { ExistingHabitId = Guid.NewGuid() } }, language = "en" });
        response.Status.Should().Be(400);
        response.Body.GetProperty("errorCode").GetString().Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        response.Body.GetProperty("draftIndex").GetInt32().Should().Be(1);
        _database.Context.PendingAgentOperations.Should().BeEmpty();
        _database.Context.Habits.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task PreviewAll_InvalidCount_Returns400AndStagesNothing(int count)
    {
        var response = await Post("/api/chat/habit-suggestions/preview-all", new
        { drafts = Enumerable.Range(0, count).Select(index => Draft($"Habit {index}", index + 10)).ToArray(), language = "en" });
        response.Status.Should().Be(400);
        _database.Context.PendingAgentOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewAll_ReadOnlyCredential_IsDeniedAndStagesNothing()
    {
        var response = await Post("/api/chat/habit-suggestions/preview-all", new { drafts = new[] { Draft(), Draft("Walk", 21) } }, readOnly: true);
        response.Status.Should().Be(200);
        var result = JsonSerializer.Deserialize<AgentExecuteOperationResponse>(response.Body, Json)!;
        result.Operation.Status.Should().Be(AgentOperationStatus.Denied);
        result.PendingOperation.Should().BeNull();
        _database.Context.PendingAgentOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewAll_MissingReadGoalsScope_IsForbiddenBeforeStaging()
    {
        var response = await Post("/api/chat/habit-suggestions/preview-all", new { drafts = new[] { Draft() } },
            scope: $"{AgentScopes.ChatInteract} {AgentScopes.ReadHabits}");
        response.Status.Should().Be(403);
        _database.Context.PendingAgentOperations.Should().BeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _database.Dispose();
    }
}
