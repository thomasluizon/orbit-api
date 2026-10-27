using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class AstraBlockAnswerServiceTests
{
    private IssuedAstraBlock Questions()
    {
        var block = IssuedAstraBlock.Create(UserId, new("question", "", [], [], Questions:
        [
            new("choice", "singleChoice", "When?", [new("a", "Morning"), new("b", "Evening")], true),
            new("scale", "scale", "Energy?", LowLabel: "Low", HighLabel: "High"),
            new("slider", "slider", "Duration?", Min: 0, Max: 1, Step: 0.1m, Unit: "hours"),
            new("yes", "yesNo", "Ready?", AllowFreeText: true)
        ]));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        return block;
    }

    private static AstraBlockAnswer QuestionAnswer(Guid id) => new(id, [], Answers:
        [new("choice", ["a"]), new("scale", Number: 3), new("slider", Number: 0.3m), new("yes", Boolean: false)]);

    [Fact]
    public async Task QuestionAnswer_PreparesTypedDataAndReservesAllowanceOnce()
    {
        var block = Questions();
        var answer = QuestionAnswer(block.Id);
        var result = await Service.PrepareAsync(Request(block.Id, []) with { BlockAnswer = answer }, default);
        result.IsSuccess.Should().BeTrue();
        using var data = JsonDocument.Parse(result.Value.Message);
        var values = data.RootElement.GetProperty("answers");
        values[0].GetProperty("selectedIds")[0].GetString().Should().Be("a");
        values[1].GetProperty("number").GetInt32().Should().Be(3);
        values[2].GetProperty("number").GetDecimal().Should().Be(0.3m);
        values[3].GetProperty("boolean").GetBoolean().Should().BeFalse();
        data.RootElement.GetProperty("questions")[0].GetProperty("options")[0].GetProperty("label").GetString().Should().Be("Morning");
        await _quota.Received(1).TryConsumeAiMessage(UserId, _work, Arg.Any<CancellationToken>());
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("unknownOption")]
    [InlineData("singleMany")]
    [InlineData("wrongType")]
    [InlineData("scaleBounds")]
    [InlineData("scaleFraction")]
    [InlineData("sliderBounds")]
    [InlineData("sliderStep")]
    [InlineData("freeTextDisabled")]
    [InlineData("freeTextLong")]
    [InlineData("mixedAnswer")]
    [InlineData("legacyIds")]
    [InlineData("legacyText")]
    [InlineData("null")]
    public async Task QuestionAnswer_InvalidDataCannotClaimBlockOrChargeAllowance(string defect)
    {
        var block = Questions();
        var answer = QuestionAnswer(block.Id);
        var values = answer.Answers!.ToArray();
        switch (defect)
        {
            case "missing": answer = answer with { Answers = values[..^1] }; break;
            case "duplicate": values[1] = values[0]; break;
            case "foreign": values[0] = values[0] with { QuestionId = "foreign" }; break;
            case "unknownOption": values[0] = values[0] with { SelectedIds = ["unknown"] }; break;
            case "singleMany": values[0] = values[0] with { SelectedIds = ["a", "b"] }; break;
            case "wrongType": values[1] = new("scale", Boolean: true); break;
            case "scaleBounds": values[1] = new("scale", Number: 6); break;
            case "scaleFraction": values[1] = new("scale", Number: 2.5m); break;
            case "sliderBounds": values[2] = new("slider", Number: -1); break;
            case "sliderStep": values[2] = new("slider", Number: 0.35m); break;
            case "freeTextDisabled": values[1] = new("scale", FreeText: "Other"); break;
            case "freeTextLong": values[0] = new("choice", FreeText: new string('a', 501)); break;
            case "mixedAnswer": values[3] = new("yes", Boolean: false, FreeText: "Other"); break;
            case "legacyIds": answer = answer with { SelectedIds = ["a"] }; break;
            case "legacyText": answer = answer with { FreeText = "Other" }; break;
            case "null": values[0] = null!; break;
        }
        if (defect != "missing") answer = answer with { Answers = values };
        var result = await Service.PrepareAsync(Request(block.Id, []) with { BlockAnswer = answer }, default);
        result.ErrorCode.Should().Be(ErrorCodes.AstraBlockInvalidSelection);
        await _store.DidNotReceiveWithAnyArgs().MarkAnsweredAsync(default, default, default, default);
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task QuestionAnswer_ReplayNegotiatesStoredJsonAgainstCurrentClient(bool supported, bool disabled, bool partsDisabled)
    {
        var block = Questions();
        var answer = QuestionAnswer(block.Id);
        block.MarkAnswered(block.CreatedAtUtc);
        typeof(IssuedAstraBlock).GetProperty(nameof(block.AnswerJson))!.SetValue(block, IssuedAstraBlock.SerializeAnswer(answer));
        typeof(IssuedAstraBlock).GetProperty(nameof(block.ResponseJson))!.SetValue(block,
            JsonSerializer.Serialize(new ChatResponse(null, [], Parts:
                [new ChatBlockPart("question", new AstraQuestionBlock(Guid.NewGuid(), block.ReadDefinition().Questions!, block.ExpiresAtUtc))]),
                ChatStreamEvent.SerializerOptions));
        _featureFlags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(disabled ? [FeatureFlagKeys.AstraAskBlocksDisabled] : partsDisabled ? [FeatureFlagKeys.AstraPartsDisabled] : []);
        var request = Request(block.Id, []) with { BlockAnswer = answer, ClientContext = new(SupportsParts: true, PartKinds: supported ? ["question"] : ["choice"]) };

        var result = await Service.PrepareAsync(request, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsReplay.Should().BeTrue();
        if (supported && !disabled && !partsDisabled)
            result.Value.Response!.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be("question");
        else
        {
            result.Value.Response!.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
            result.Value.Response.AiMessage.Should().Contain("Energy?").And.Contain("Morning").And.Contain("Low");
        }
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
        var changed = await Service.PrepareAsync(request with
        { BlockAnswer = answer with { Answers = [new("choice", ["b"]), .. answer.Answers!.Skip(1)] } }, default);
        changed.ErrorCode.Should().Be(ErrorCodes.AstraBlockAnswered);
    }

    [Theory]
    [InlineData("I want to hurt myself")]
    [InlineData("Quero me machucar")]
    public async Task QuestionAnswer_CustomCrisisTextBypassesQuotaAndModeration(string text)
    {
        var block = Questions();
        var answer = QuestionAnswer(block.Id) with { Answers = [new("choice", FreeText: text), .. QuestionAnswer(block.Id).Answers!.Skip(1)] };
        var checkedInput = false;
        var result = await Service.PrepareAsync(Request(block.Id, []) with { BlockAnswer = answer }, default,
            (_, _) => { checkedInput = true; throw new InvalidOperationException(); });
        result.IsSuccess.Should().BeTrue();
        result.Value.Crisis.Should().NotBe(CrisisLocales.None);
        checkedInput.Should().BeFalse();
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }
}
