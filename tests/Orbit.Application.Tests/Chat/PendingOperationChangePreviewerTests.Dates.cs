using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed partial class PendingOperationChangePreviewerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OneTimePreview_AlwaysCarriesEditableDateWithHonestProvenance(bool bulk, bool requested)
    {
        var source = requested
            ? """{"title":"Terraço Itália","frequency_unit":"OneTime","due_date":"2026-12-31","due_time":"20:30"}"""
            : """{"title":"Reservation","frequency_unit":"OneTime"}""";
        var previewer = new Orbit.Application.Chat.PendingOperationChangePreviewer(_habits, _goals, _tags, _userDateService,
            new Orbit.Application.Chat.Tools.AiToolRegistry([
                new Orbit.Application.Chat.Tools.Implementations.CreateHabitTool(_userDateService, NSubstitute.Substitute.For<MediatR.IMediator>()),
                new Orbit.Application.Chat.Tools.Implementations.BulkCreateHabitsTool(NSubstitute.Substitute.For<MediatR.IMediator>())]));
        var result = await previewer.PreviewAsync(UserId, bulk ? "bulk_create_habits" : "create_habit",
            JsonSerializer.Deserialize<JsonElement>(bulk ? "{\"habits\":[" + source + "]}" : source));
        var preview = result.Value;

        preview.Should().NotBeNull();
        var field = preview!.Items!.Single().Fields.Should().ContainSingle(field => field.Field == "due_date").Which;
        field.ValueType.Should().Be("date");
        field.IsEditable.Should().BeTrue();
        field.ProposedValue!.Value.GetString().Should().Be(requested ? "2026-12-31" : "2026-09-25");
        field.Provenance.Should().Be(requested ? "requested" : "suggested");
        if (requested)
            preview.Items!.Single().Fields.Should().Contain(field => field.Field == "due_time" && field.NewValue == "20:30"
                && field.Provenance == "requested");
    }
}
