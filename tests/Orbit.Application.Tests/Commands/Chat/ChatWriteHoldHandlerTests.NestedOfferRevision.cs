using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    public static TheoryData<string, string> NestedOfferDrafts => new()
    {
        { "create_habit", """{"title":"Wellness routine","frequency_unit":"Day","frequency_quantity":1,"sub_habits":[{"title":"Mobility","due_time":"07:00","emoji":"🧘","suggested_fields":["emoji"]},{"title":"Reading","due_time":"21:00","emoji":"📚","suggested_fields":["emoji"]}],"suggested_fields":["frequency_unit","frequency_quantity"],"orbit_write_complete":true}""" },
        { "create_habit", """{"title":"Wellness routine","frequency_unit":"Day","frequency_quantity":1,"emoji":"🌿","sub_habits":[{"title":"Mobility","due_time":"07:00"},{"title":"Reading","due_time":"21:00"}],"suggested_fields":["emoji","sub_habits"],"orbit_write_complete":true}""" },
        { "create_habit", """{"title":"Wellness routine","frequency_unit":"Day","frequency_quantity":1,"sub_habits":[{"title":"Mobility","due_time":"07:00","repeat_reminder":{"interval_minutes":120},"reminder_enabled":true,"suggested_fields":["repeat_reminder","reminder_enabled"]},{"title":"Reading","due_time":"21:00","reminder_enabled":true,"scheduled_reminders":[{"when":"same_day","time":"20:45"}],"suggested_fields":["reminder_enabled","scheduled_reminders"]}],"suggested_fields":["frequency_unit","frequency_quantity","sub_habits"],"orbit_write_complete":true}""" },
        { "create_habit", """{"title":"Wellness routine","frequency_unit":"Day","frequency_quantity":1,"due_time":"07:00","reminder_enabled":true,"reminder_times":[10],"sub_habits":[{"title":"Mobility","due_time":"07:00"},{"title":"Reading","due_time":"21:00"}],"suggested_fields":["reminder_enabled","reminder_times"],"orbit_write_complete":true}""" },
        { "bulk_create_habits", """{"habits":[{"title":"Wellness routine","frequency_unit":"Day","frequency_quantity":1,"sub_habits":[{"title":"Mobility","due_time":"07:00","reminder_enabled":true,"reminder_times":[15],"suggested_fields":["reminder_enabled","reminder_times"]},{"title":"Reading","due_time":"21:00","emoji":"📚","suggested_fields":["emoji"]}]}],"orbit_write_complete":true}""" }
    };

    [Theory]
    [MemberData(nameof(NestedOfferDrafts))]
    public async Task NestedOfferPreview_RevisesWithTheFingerprintTheChatResponseReturned(string operation, string draft)
    {
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userDateService.GetUserTimeZoneAsync(_userId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        ScriptToolCall(draft, operation);
        var client = new AgentClientContext(Locale: "en", SupportsParts: true, SupportsPendingOperationChanges: true,
            SupportsProactiveOffers: true, ConversationId: Guid.NewGuid());

        var chat = await CreateHandler().Handle(new ProcessUserChatCommand(_userId,
            "Create a daily wellness routine: mobility in the morning and reading at night, each tracked separately.",
            ClientContext: client), default);

        chat.IsSuccess.Should().BeTrue(chat.Error);
        var pending = chat.Value.PendingOperations.Should().ContainSingle().Which;
        var item = pending.Items!.Single();
        var suggested = item.Fields.First(field => field.Removal is not null);
        var service = new PendingOperationRevisionService(_pendingOperationStore, _previewer,
            new RevisePendingOperationRequestValidator(), _toolRegistry);
        var revised = await service.ReviseAsync(_userId, pending.Id, new(pending.PreviewFingerprint!,
            [new(item.ItemId, RemoveSuggestedFields: [suggested.Field])], client), default);

        revised.IsSuccess.Should().BeTrue(revised.Error);
        revised.Preview!.Items!.Single().Fields.Should().NotContain(field => field.Field == suggested.Field);
    }

    [Theory]
    [InlineData("Create a daily wellness routine: mobility in the morning and reading at night, each tracked separately.", "requested")]
    [InlineData("Crie uma rotina diária de bem-estar: mobilidade pela manhã e leitura à noite, com acompanhamento separado.", "requested")]
    [InlineData("Create a wellness routine: mobility in the morning and reading at night.", "suggested")]
    public async Task NestedOfferPreview_RequestedScheduleNeverShowsAsSuggested(string message, string provenance)
    {
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userDateService.GetUserTimeZoneAsync(_userId, Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        ScriptToolCall(NestedOfferDrafts.First()[1].ToString()!, "create_habit");
        var client = new AgentClientContext(Locale: "en", SupportsParts: true, SupportsPendingOperationChanges: true,
            SupportsProactiveOffers: true, ConversationId: Guid.NewGuid());

        var chat = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message, ClientContext: client), default);

        chat.IsSuccess.Should().BeTrue(chat.Error);
        var fields = chat.Value.PendingOperations.Should().ContainSingle().Which.Items!.Single().Fields;
        fields.Single(field => field.Field == "frequency_unit").Provenance.Should().Be(provenance);
        fields.Single(field => field.Field == "frequency_quantity").Provenance.Should().Be(provenance);
    }
}
