using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task PendingPreview_GroundingIsEnforcedWithoutTheGlobalFlagAndUsesOnlyPreviewFacts()
    {
        SetupExistingParents("en");
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        HoldReplayWrites(bulk);
        SetupAiResponse(ReplayCall("bulk_create_habits", """{"habits":[{"title":"Take vitamin D","frequency_unit":"Week","frequency_quantity":1}]}""").Value);
        ReplyToReplay("Yes, they are sub-habits of Before Bed and Post Lunch, every 99 days.");

        var result = await handler.Handle(new(UserId, "Create a weekly vitamin D habit."), default);

        result.Value.PendingOperations.Should().ContainSingle();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.GroundingFacts.Facts.Should().NotContain(fact => fact.Value == "99"
            || fact.Kind == "EntityName" && (fact.Value == "BEFORE BED" || fact.Value == "POST LUNCH"));
        var warnings = _logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning).Select(call =>
                ((IEnumerable<KeyValuePair<string, object?>>)call.GetArguments()[2]!).ToDictionary()).ToArray();
        warnings.Should().Contain(properties => properties.ContainsKey("ViolationKind")
            && Equals(properties["Enforced"], true));
    }

    [Theory]
    [InlineData("en", "Post lunch: take vitamin D (once a week) Before bed: take rovustatina (everyday)")]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana) Before bed: tomar rovustatina (todo dia)")]
    [InlineData("en", "POST LUNCH | take vitamin D (once a week) BEFORE BED = take rovustatina (everyday)")]
    [InlineData("en", "Post lunc: take vitamin D (once a week) Befor bed: take rovustatina (everyday)")]
    [InlineData("en", "Post lunch - take vitamin D (once a week) Before bed -> take rovustatina (everyday)")]
    [InlineData("en", "Post lunch / take vitamin D (once a week) Before bed > take rovustatina (everyday)")]
    public async Task ExistingHeadings_OnePreviewBindsEachItemToItsAccountParent(string language, string message)
    {
        var parents = SetupExistingParents(language);
        var bulk = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(bulk);
        var held = HoldReplayWrites(bulk);
        var vitamin = language == "en" ? "Take vitamin D" : "Tomar vitamina D";
        var statin = language == "en" ? "Take rovustatina" : "Tomar rovustatina";
        SetupAiResponse(ReplayCall("bulk_create_habits", JsonSerializer.Serialize(new
        {
            habits = new[]
            {
                new { title = vitamin, description = "After lunch", frequency_unit = "Week", frequency_quantity = 1 },
                new { title = statin, description = "Before bed", frequency_unit = "Day", frequency_quantity = 1 }
            }
        })).Value);
        ReplyToReplay("Yes, these will be sub-habits. Proposal: nothing saved.");

        var result = await handler.Handle(new(UserId, message), default);

        result.IsSuccess.Should().BeTrue();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(2);
        for (var index = 0; index < 2; index++)
        {
            pending.Items![index].Fields.Should().ContainSingle(field => field.Field == "parent_habit_id")
                .Which.NewValue.Should().Be(parents[index].Title);
            pending.Items[index].Fields.Should().NotContain(field => field.Field == "description");
            held.Single().Arguments.GetProperty("habits")[index].GetProperty("parent_habit_id").GetGuid().Should().Be(parents[index].Id);
        }
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
    }

    private Habit[] SetupExistingParents(string language)
    {
        SetupResolutionAccount();
        var user = User.Create("Test", "parents@example.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var parents = new[] { CreateHabit("Post Lunch"), CreateHabit("Before Bed") };
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => parents.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        return parents;
    }
}
