using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class G10CompositionGuidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuredReadGuidanceCoversRemindersCoachingMetricsChoicesAndHistory(bool composition)
    {
        var builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest([], UserToday: new DateOnly(2026, 10, 30), BlockFirstComposition: composition);
        var prompt = builder.BuildStatic(request) + builder.BuildDynamic(request);

        prompt.Should().Contain("A reminder asking what remains today uses query_habits with date=today and is_completed=false; it does not require choosing a reminder or a habit.");
        prompt.Should().Contain("An identified activity that the person wants to make easier to start uses suggest_breakdown with one_small_step=true, without asking them to select an existing habit.");
        prompt.Should().Contain("Every option presented as an existing account record must come from the account index or a successful read; never invent an alternative record.");
        prompt.Should().Contain("For a named habit's completion rate or completion total, resolve its ID with query_habits and orbit_read_complete=false, then call get_habit_metrics.");
        prompt.Should().Contain("include_metrics on query_habits does not replace get_habit_metrics for a completion question.");
        prompt.Should().Contain("A single discrete selection always uses ask form choice with top-level question and options, including when the question also requests a free-text change.");
        prompt.Should().Contain("A current streak total does not establish historical missed days. Never label days before a habit's creation as not logged or missed, or infer those states from a current streak alone.");
    }

    [Fact]
    public void SuggestionExclusionsPreserveRequestedTopics()
    {
        var tool = new SuggestHabitsTool(Substitute.For<IMediator>());
        tool.Description.Should().Contain("Keep the person's requested topics in instruction; never put those topics in excluded_activities for ordinary suggestions.");
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        schema.GetProperty("properties").GetProperty("excluded_activities").GetProperty("description").GetString()
            .Should().Contain("For ordinary suggestions, omit this field or use null; requested topics are not exclusions.");
    }
}
