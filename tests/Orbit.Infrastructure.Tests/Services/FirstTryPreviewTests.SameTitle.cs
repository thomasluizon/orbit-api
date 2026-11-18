using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// The same-title detail on each route that returns a preview item, over one SQLite database. The account holds a
/// "Morning" habit with two "Water" sub-habits. Each test targets the second one: "Morning, 2 of 2".
/// </summary>
public sealed partial class FirstTryPreviewTests
{
    private const string Detail = "Morning, 2 of 2";
    private const string TitleForm = "Water (Morning, 2 of 2)";

    private static readonly AgentClientContext DetailClient = PlainClient with { SupportsPendingOperationEntityDetail = true };

    private Habit AddPlaced(string title, int position, Guid? parent = null)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, title, FrequencyUnit.Day, 1, Today,
            ParentHabitId: parent, Position: position)).Value;
        _factory.Context.Habits.Add(habit);
        _factory.Context.SaveChanges();
        _factory.Context.ChangeTracker.Clear();
        return habit;
    }

    /// <summary>The "Morning" habit and its two "Water" sub-habits, in the app list order.</summary>
    private (Habit First, Habit Second) AddWaterPair()
    {
        var morning = AddPlaced("Morning", 1);
        return (AddPlaced("Water", 1, morning.Id), AddPlaced("Water", 2, morning.Id));
    }

    private static void HasDetail(PendingOperationItem item, bool withFlag)
    {
        item.EntityName.Should().Be(withFlag ? "Water" : TitleForm);
        item.EntityDetail.Should().Be(withFlag ? Detail : null);
        item.Fields.Should().OnlyContain(field => field.EntityName == "Water");
    }

    /// <summary>The preview that the store holds for the operation: the plain title and no detail.</summary>
    private void StoredPreviewIsPlain(Guid pendingId)
    {
        _factory.Context.ChangeTracker.Clear();
        var row = _factory.Context.PendingAgentOperations.AsNoTracking().Single(operation => operation.Id == pendingId);
        row.RevisedPreviewJson.Should().NotBeNull();
        row.RevisedPreviewJson.Should().NotContain("of 2").And.NotContainEquivalentOf("entityDetail");
        row.ArgumentsJson.Should().NotContain("of 2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_GivesTheDetailAndStoresThePlainPreview(bool withFlag)
    {
        var (_, water) = AddWaterPair();
        var bound = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { water.Id } } }, PlainClient);
        bound.Items!.Single().EntityName.Should().Be("Water", "the executor returns the plain item");

        var result = await _controller.RefreshPendingOperationPreview(bound.Id, default,
            JsonSerializer.Serialize(withFlag ? DetailClient : PlainClient, JsonSerializerOptions.Web));

        var refreshed = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PendingOperationRevisionResult>().Subject;
        HasDetail(refreshed.Preview!.Items!.Single(), withFlag);
        refreshed.Preview.PreviewFingerprint.Should().Be(bound.PreviewFingerprint);
        StoredPreviewIsPlain(bound.Id);
    }

    [Fact]
    public async Task Refresh_WithNoClientContextGivesTheTitleForm()
    {
        var (_, water) = AddWaterPair();
        var bound = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { water.Id } } }, PlainClient);

        var result = await _controller.RefreshPendingOperationPreview(bound.Id, default);

        HasDetail(result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PendingOperationRevisionResult>()
            .Subject.Preview!.Items!.Single(), withFlag: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revise_GivesTheDetailKeepsTheTitleValuePlainAndStoresThePlainPreview(bool withFlag)
    {
        var (_, water) = AddWaterPair();
        var client = withFlag ? DetailClient : PlainClient;
        var bound = await BindAsync("update_habit", new { habit_id = water.Id, title = "Agua" }, client);

        var result = await _controller.RevisePendingOperation(bound.Id, new RevisePendingOperationRequest(bound.PreviewFingerprint!,
            [new RevisedPendingOperationItem(bound.Items![0].ItemId, JsonSerializer.SerializeToElement(new { title = "Agua fria" }))],
            client), default);

        var revised = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PendingOperationRevisionResult>().Subject;
        var item = revised.Preview!.Items!.Single();
        HasDetail(item, withFlag);
        item.Fields.Single(field => field.Field == "title").NewValue.Should().Be("Agua fria");
        JsonSerializer.Serialize(revised, JsonSerializerOptions.Web).Should().NotContain("→", "a revise has no answer text");
        StoredPreviewIsPlain(bound.Id);
        Succeeded(await ApproveAsync(bound.Id, client));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolve_ReadsTheFlagOfTheStashedChatContext(bool withFlag)
    {
        var (_, water) = AddWaterPair();
        var stash = await _clarifications.CreateAsync(_userId, "update_habit",
            JsonSerializer.Serialize(new { habit_id = water.Id }), "description", "Which note?",
            JsonSerializer.Serialize(new[] { new QuickAction("glass", """{"description":"One glass"}""") }),
            clientContext: withFlag ? DetailClient : PlainClient);

        var resolved = await _controller.ResolveClarification(stash,
            new ResolveClarificationRequest("""{"description":"One glass"}"""), default);

        var preview = Pending(resolved.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeAssignableTo<AgentExecuteOperationResponse>().Subject);
        HasDetail(preview.Items!.Single(), withFlag);
        var stored = _store.GetExecution(_userId, preview.Id)!;
        (await _previewer.PreviewAsync(_userId, stored.OperationId, stored.Arguments, default, PlainClient))
            .Value!.PreviewFingerprint.Should().Be(preview.PreviewFingerprint, "the detail is not a fingerprint input");
        Succeeded(await ApproveAsync(preview.Id, PlainClient));
    }

    /// <summary>
    /// An execute returns a new pending operation with items when the operation needs a step-up. The approval of a
    /// delete of the second "Water" gives that preview.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_GivesTheDetailOnTheStepUpPreview(bool withFlag)
    {
        var (_, water) = AddWaterPair();
        var client = withFlag ? DetailClient : PlainClient;
        var bound = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { water.Id } } }, client);
        _bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConfirmationRequirement.StepUp);

        var result = await ApproveAsync(bound.Id, client);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AgentExecuteOperationResponse>().Subject;
        response.Operation.PolicyReason.Should().Be("step_up_required", JsonSerializer.Serialize(response));
        HasDetail(response.PendingOperation!.Items!.Single(), withFlag);
        (await _factory.Context.Habits.AsNoTracking().CountAsync(habit => habit.Title == "Water")).Should().Be(2);
    }

    /// <summary>
    /// A partial <c>bulk_create_habits</c>: the nest limit drops after the approval checks, so an item fails at the
    /// execution and the execute response holds the retry preview. A reuse item never takes the partial path
    /// (<c>BulkCreateHabitsTool.ExecuteCoreAsync</c>), so a retry preview holds new records only and gets no detail.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_RetryPreviewOfAPartialBulkCreateHoldsNewRecordsAndNoDetail(bool withFlag)
    {
        AddWaterPair();
        var client = withFlag ? DetailClient : PlainClient;
        var bound = await BindAsync("bulk_create_habits", new
        {
            habits = new object[]
            {
                new { title = "Water", frequency_unit = "Day", frequency_quantity = 1, sub_habits = new[] { new { title = "Sip" } } },
                new { title = "Journal", frequency_unit = "Day", frequency_quantity = 1 }
            }
        }, client);
        var config = _provider.GetRequiredService<IAppConfigService>();
        _executor.BeforeNextExecute = () =>
        {
            config.GetAsync(AppConfigKeys.MaxHabitDepth, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);
            return Task.CompletedTask;
        };

        var result = await ApproveAsync(bound.Id, client);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AgentExecuteOperationResponse>().Subject;
        response.PendingOperation.Should().NotBeNull("a partial bulk create returns a retry preview: " + JsonSerializer.Serialize(response));
        response.PendingOperation!.Id.Should().NotBe(bound.Id);
        var items = response.PendingOperation.Items!;
        items.Should().Contain(item => item.EntityName == "Water")
            .And.OnlyContain(item => item.EntityId == null && item.EntityDetail == null && !item.EntityName.Contains('('));
    }

    [Fact]
    public async Task StepUpVerify_ReturnsAPendingOperationWithNoItem()
    {
        var (_, water) = AddWaterPair();
        _bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConfirmationRequirement.StepUp);
        var bound = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { water.Id } } }, PlainClient);
        bound.Items!.Single().EntityId.Should().Be(water.Id, "the bound preview holds the same-title record");
        string? code = null;
        var email = Substitute.For<IEmailService>();
        email.SendVerificationCodeAsync(Arg.Any<string>(), Arg.Do<string>(value => code = value), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new AgentStepUpService(_factory.Context, email, Options.Create(new AgentPlatformSettings()));
        _stepUp.VerifyChallengeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => service.VerifyChallengeAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<Guid>(2),
                call.ArgAt<string>(3), call.ArgAt<CancellationToken>(4)));
        var challenge = await service.IssueChallengeAsync(_userId, bound.Id, "en", default);
        challenge.IsSuccess.Should().BeTrue(challenge.Error);

        var result = await _controller.VerifyPendingOperationStepUp(bound.Id,
            new AiController.VerifyStepUpRequest(challenge.Value.ChallengeId, code!), default);

        var verified = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PendingAgentOperation>().Subject;
        verified.Id.Should().Be(bound.Id);
        verified.Items.Should().BeNull("the store fills no item on a step-up verify");
        verified.Changes.Should().BeNull();
    }

    [Theory]
    [InlineData("en", "Water (Morning, 2 of 2)")]
    [InlineData("pt-BR", "Water (Morning, 2º de 2)")]
    public async Task HabitSuggestionPreviews_WriteTheTitleFormBecauseTheyHaveNoFlag(string language, string name)
    {
        var (_, water) = AddWaterPair();

        var single = await new PreviewHabitSuggestionCommandHandler(_suggestions, _executor, _sameTitleDetail)
            .Handle(new(_userId, Draft("Water", water.Id, new TimeOnly(6, 0)), language), default);
        var all = await new PreviewAllHabitSuggestionsCommandHandler(_suggestions, _executor, _changeSets, _store, _sameTitleDetail)
            .Handle(new(_userId, [Draft("Water", water.Id, new TimeOnly(7, 0)), Draft("Journal")], language), default);

        single.IsSuccess.Should().BeTrue(single.Error);
        all.IsSuccess.Should().BeTrue(all.Error);
        var item = Pending(single.Value).Items!.Single();
        item.EntityName.Should().Be(name);
        item.EntityDetail.Should().BeNull();
        var set = Pending(all.Value);
        set.Items!.Single(member => member.EntityId == water.Id).EntityName.Should().Be(name);
        set.Items!.Single(member => member.EntityId != water.Id).EntityName.Should().NotContain("(");
        var stored = _store.GetExecution(_userId, set.Id)!;
        stored.PreviewFingerprint.Should().Be(set.PreviewFingerprint);
        Succeeded(await ApproveAsync(set.Id, PlainClient));
    }

    /// <summary>
    /// Two open previews on one same-title group. The first one executes, so the group gets smaller. The second one
    /// is still current, because no fingerprint holds the place.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task SecondOpenPreviewOnTheGroup_StaysApprovableAfterTheFirstOneExecutes(bool changeSet, bool withFlag)
    {
        var morning = AddPlaced("Morning", 1);
        var waters = Enumerable.Range(1, 4).Select(position => AddPlaced("Water", position, morning.Id)).ToArray();
        var client = withFlag ? DetailClient : PlainClient;
        var first = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { waters[0].Id } } }, client);
        PendingAgentOperation second;
        if (changeSet)
        {
            var rename = await BindAsync("update_habit", new { habit_id = waters[2].Id, title = "Agua" }, client);
            var delete = await BindAsync("bulk_delete_habits", new { filter = new { habit_ids = new[] { waters[3].Id } } }, client);
            second = (await _changeSets.CombineAsync(_userId, [rename, delete], default, client))!.PendingOperation;
        }
        else
            second = await BindAsync("update_habit", new { habit_id = waters[3].Id, title = "Agua" }, client);
        var before = (await _sameTitleDetail.WriteAsync(_userId, second, client, default)).Items!
            .Select(item => withFlag ? item.EntityDetail : item.EntityName).ToArray();

        Succeeded(await ApproveAsync(first.Id, client));

        var after = (await _sameTitleDetail.WriteAsync(_userId, second, client, default)).Items!
            .Select(item => withFlag ? item.EntityDetail : item.EntityName).ToArray();
        before.Should().OnlyContain(text => text!.Contains("of 4"));
        after.Should().OnlyContain(text => text!.Contains("of 3"), "the detail is the detail of the present time");
        var refreshed = await _controller.RefreshPendingOperationPreview(second.Id, default,
            JsonSerializer.Serialize(client, JsonSerializerOptions.Web));
        refreshed.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<PendingOperationRevisionResult>()
            .Which.Preview!.PreviewFingerprint.Should().Be(second.PreviewFingerprint);
        Succeeded(await ApproveAsync(second.Id, client));
        (await _factory.Context.Habits.AsNoTracking().CountAsync(habit => habit.Title == "Agua")).Should().Be(1);
    }
}
