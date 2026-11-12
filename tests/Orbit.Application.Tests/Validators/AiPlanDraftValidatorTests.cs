using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Validators;

public class AiPlanDraftValidatorTests
{
    [Theory]
    [InlineData("{\"title\":\"Progress\",\"steps\":null}")]
    [InlineData("{\"title\":\"Progress\",\"steps\":[null,null]}")]
    [InlineData("{\"title\":\"Progress\",\"steps\":[]}")]
    [InlineData("{\"title\":\"Progress\",\"steps\":[{\"text\":\"Read habits\",\"tool\":\"query_habits\",\"phase\":\"execution\"}]}")]
    public void InvalidModelPlansFailValidationWithoutThrowing(string json)
    {
        var draft = JsonSerializer.Deserialize<AiPlanDraft>(json, AiPlan.JsonOptions)!;
        new AiPlanDraftValidator().Validate(draft).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("Read\nhabits")]
    [InlineData("Read habits and every detail of their history")]
    public void RowsMustFitTheSingleLineContract(string text)
    {
        var draft = new AiPlanDraft("Progress", [new(text, "query_habits", "execution"), new("Read goals", "query_goals", "execution")]);
        new AiPlanDraftValidator().Validate(draft).IsValid.Should().BeFalse();
    }
}
