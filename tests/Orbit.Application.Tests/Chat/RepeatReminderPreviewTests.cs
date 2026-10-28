using FluentAssertions;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.ValueObjects;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class RepeatReminderPreviewTests
{
    [Fact]
    public void Preview_AcceptsTheValueProducedForAnUnspecifiedStartInstant()
    {
        var value = JsonSerializer.Serialize(RepeatReminderToolArguments.Value(new RepeatReminderOptions(480)));
        RepeatReminderPreview.Format(value, new AgentClientContext(Locale: "pt-BR"), TimeZoneInfo.Utc)
            .Should().Be("A cada 8 horas");
    }

    [Theory]
    [InlineData("en", "Every 8 hours; active 22:00 to 06:00")]
    [InlineData("pt-BR", "A cada 8 horas; das 22:00 às 06:00")]
    public void Preview_UsesHumanCopyAndKeepsTheOvernightWindow(string locale, string expected)
    {
        RepeatReminderPreview.Format("{\"interval_minutes\":480,\"active_start\":\"22:00\",\"active_end\":\"06:00\"}",
            new AgentClientContext(Locale: locale), TimeZoneInfo.Utc).Should().Be(expected);
    }
}
