using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task HabitTitles_RequestLanguageReachesModelAndPreview(string language, bool coaching)
    {
        ParentAccount(language == "en" ? "pt-BR" : "en");
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        var builder = new SystemPromptBuilder();
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns(call => builder.BuildStatic(call.Arg<PromptBuildRequest>()));
        _promptBuilder.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns(call => builder.BuildDynamic(call.Arg<PromptBuildRequest>()));
        var portuguese = language == "pt-BR";
        var titles = coaching
            ? new[] { portuguese ? "Separar roupa e tênis para caminhar" : "Set out walking clothes and shoes" }
            : portuguese ? new[] { "tomar vitamina D", "tomar rosuvastatina" }
                : new[] { "take vitamin D", "take rosuvastatin" };
        var message = coaching
            ? portuguese ? "Crie uma tarefa para amanhã: separar roupa e tênis para caminhar." : "Create a task for tomorrow: set out walking clothes and shoes."
            : portuguese ? "Depois do almoço: tomar vitamina D (uma vez por semana) Antes de dormir: tomar rosuvastatina (todo dia)"
                : "Post lunch: take vitamin D (once a week) Before bed: take rosuvastatin (everyday)";
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            habits = titles.Select((title, index) => new { title, frequency_unit = coaching ? "OneTime" : index == 0 ? "Week" : "Day" }).ToArray()
        }), "bulk_create_habits");

        var result = await CreateHandler().Handle(new(_userId, message,
            ClientContext: new AgentClientContext(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items!
            .Select(item => item.EntityName).Should().Equal(titles);
        var sent = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Single();
        sent.SystemPrompt.Should().Contain("Preserve a title the person quoted or named verbatim");
        var languageRule = "Write composed habit titles in " + (portuguese ? "Brazilian Portuguese" : "English");
        sent.DynamicContext.Should().Contain(languageRule);
        sent.SystemPrompt.Should().Contain("use the request language supplied for this turn")
            .And.Contain("Preserve a title the person quoted or named verbatim");
    }

    [Theory]
    [InlineData("create_habit", "pt-BR", "Crie a tarefa 'Read 2026-10-27'.", "Read 2026-10-27")]
    [InlineData("create_habit", "en", "Create a task named 'Ler 2026-10-27'.", "Ler 2026-10-27")]
    [InlineData("update_habit", "pt-BR", "Renomeie Post Lunch para 'Read 2026-10-27'.", "Read 2026-10-27")]
    [InlineData("update_habit", "en", "Rename Post Lunch to 'Ler 2026-10-27'.", "Ler 2026-10-27")]
    public async Task HabitTitles_NamedTitleSurvivesPreviewNormalization(string operation, string locale, string message, string title)
    {
        var parents = ParentAccount(locale);
        var arguments = operation == "create_habit"
            ? JsonSerializer.Serialize(new { title, frequency_unit = "OneTime" })
            : JsonSerializer.Serialize(new { habit_id = parents[0].Id, title });
        ScriptToolCall(arguments, operation);

        var result = await CreateHandler().Handle(new(_userId, message, ClientContext: new AgentClientContext(Locale: locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var item = stored.Arguments.TryGetProperty("habits", out var habits) ? habits[0] : stored.Arguments;
        item.GetProperty("title").GetString().Should().Be(title);
        if (operation == "create_habit")
            pending.Items.Should().ContainSingle().Which.EntityName.Should().Be(title);
        else
            pending.Items.Should().ContainSingle().Which.Fields.Should().ContainSingle(field => field.Field == "title")
                .Which.NewValue.Should().Be(title);
    }
}
