using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class QuestionBlockContractTests
{
    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    [InlineData("openapi_v1-r2.json")]
    [InlineData("openapi_v2-r2.json")]
    public void PublishedArtifacts_ContainQuestionAndAnswerContracts(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("AstraQuestionBlock", out var block).Should().BeTrue();
        block.GetProperty("properties").TryGetProperty("questions", out _).Should().BeTrue();
        block.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().NotContain("title");
        schemas.GetProperty("AstraBlockAnswer").GetProperty("properties").TryGetProperty("answers", out _).Should().BeTrue();
        schemas.GetProperty("AstraQuestionAnswer").GetProperty("properties").TryGetProperty("boolean", out _).Should().BeTrue();
        schemas.GetProperty("AstraQuestion").GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).Should().Equal("singleChoice", "multipleChoice", "scale", "slider", "yesNo");
    }
}

public partial class ChatControllerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task QuestionAnswer_MultipartJsonPreservesTypedValues(string locale)
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));
        var json = JsonSerializer.Serialize(new
        {
            blockId = id,
            selectedIds = Array.Empty<string>(),
            answers = new[] { new { questionId = "ready", boolean = false } }
        });
        await _controller.ProcessChat("Answer", null, null, default,
            clientContext: JsonSerializer.Serialize(new { locale, supportsParts = true, partKinds = new[] { "question" } }), blockAnswer: json);
        await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command => command.BlockAnswer != null
            && command.BlockAnswer.BlockId == id && command.BlockAnswer.Answers != null
            && command.BlockAnswer.Answers.Single().Boolean == false && command.BlockAnswer.Answers.Single().QuestionId == "ready"),
            Arg.Any<CancellationToken>());
    }
}
