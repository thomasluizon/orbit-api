using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData("en", "Water")]
    [InlineData("pt-BR", "Beber água")]
    public async Task GateSeven_CapturedSuggestionDraftsRetainTheirTypedIdentityAndFields(string language, string title)
    {
        var existing = Draft(title).ToHabit(_userId).Value;
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateSeven", "composition-suggestions-duplicate-" + language + "-r1.json")));
        using var answer = JsonDocument.Parse(capture.RootElement.GetProperty("answer").GetString()!);
        var recorded = answer.RootElement.GetProperty("parts").EnumerateArray()
            .Single(part => part.GetProperty("type").GetString() == "block").GetProperty("block").GetProperty("drafts");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var drafts = recorded.EnumerateArray().Select(draft =>
        {
            var fields = JsonNode.Parse(draft.GetRawText())!.AsObject();
            fields["id"] = Guid.NewGuid().ToString();
            if (fields.ContainsKey("existingHabitId")) fields["existingHabitId"] = existing.Id.ToString();
            fields["dueDate"] = DateOnly.Parse(fields["dueDate"]!.ToString(), CultureInfo.GetCultureInfo(language))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return fields.Deserialize<HabitSuggestionDraft>(options)!;
        }).ToArray();
        Generate(drafts);
        var service = Service(existing);
        var instruction = capture.RootElement.GetProperty("toolCalls")[0].GetProperty("arguments").GetProperty("instruction").GetString()!;

        var result = await service.GenerateAsync(_userId, instruction, language, 2, null, default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().BeEquivalentTo(drafts, comparison => comparison.WithStrictOrdering());
        result.Value.Drafts[0].ExistingHabitId.Should().Be(existing.Id);
        var executor = Substitute.For<Orbit.Domain.Interfaces.IAgentOperationExecutor>();
        await new PreviewHabitSuggestionCommandHandler(service, executor).Handle(new(_userId, result.Value.Drafts[0]), default);
        await executor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.OperationId == "update_habit" && request.PreviewOnly
            && request.Arguments.GetProperty("habit_id").GetGuid() == existing.Id), Arg.Any<CancellationToken>());
        await _habits.DidNotReceive().AddAsync(Arg.Any<Orbit.Domain.Entities.Habit>(), Arg.Any<CancellationToken>());
    }
}
