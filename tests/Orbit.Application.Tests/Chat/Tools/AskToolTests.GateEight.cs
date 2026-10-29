using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class AskToolTests
{
    public static IEnumerable<object?[]> GateEightCalls()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GateEightAsk");
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var capture = JsonDocument.Parse(File.ReadAllText(file));
            var index = 0;
            foreach (var call in capture.RootElement.GetProperty("toolCalls").EnumerateArray())
            {
                if (call.GetProperty("name").GetString() == "ask")
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var error = index != 0 ? null : name switch
                    {
                        "ambiguity-en-r2" or "composition-choice-en-r2" => "questions[0].options",
                        "composition-compound-missing-kind-en-r3" => "questions",
                        _ => null
                    };
                    yield return [name, index, call.GetProperty("arguments").GetRawText(), error];
                }
                index++;
            }
        }
    }

    [Theory]
    [MemberData(nameof(GateEightCalls))]
    public async Task GateEightAsk_CapturedArgumentsHaveSpecificValidationResult(string capture, int index, string json, string? error)
    {
        using var args = JsonDocument.Parse(json);
        var result = await _tool.ExecuteAsync(args.RootElement, UserId, default);

        result.Success.Should().Be(error is null, $"{capture} call {index}: {result.Error}");
        if (error is not null)
            result.Error.Should().Contain(error);
        else
            result.Payload.Should().BeOfType<AstraBlockDefinition>();
    }
}
