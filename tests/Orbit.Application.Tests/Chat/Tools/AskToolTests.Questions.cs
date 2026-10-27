using System.Text.Json;
using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class AskToolTests
{
    [Theory]
    [InlineData("en", "Energy?", "Low", "High")]
    [InlineData("pt-BR", "Energia?", "Baixa", "Alta")]
    public async Task Question_AllTypesAndOptionalHeading_RoundTrip(string locale, string prompt, string low, string high)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            form = "question",
            intro = prompt,
            progress = new { current = 1, total = 2 },
            questions = new object[]
            {
                new { id = "single", type = "singleChoice", prompt, allowFreeText = true,
                    options = new[] { new { id = "a", label = low, recommended = true }, new { id = "b", label = high, recommended = false } } },
                new { id = "multi", type = "multipleChoice", prompt, allowFreeText = true,
                    options = new[] { new { id = "a", label = low }, new { id = "b", label = high } } },
                new { id = "scale", type = "scale", prompt, lowLabel = low, highLabel = high },
                new { id = "slider", type = "slider", prompt, min = 0, max = 30, step = 5, unit = "min" },
                new { id = "yes", type = "yesNo", prompt, allowFreeText = true }
            }
        });

        var result = await _tool.ExecuteAsync(args, UserId, default);

        result.Success.Should().BeTrue(locale);
        var definition = result.Payload.Should().BeOfType<AstraBlockDefinition>().Subject;
        var json = JsonSerializer.SerializeToElement(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("questions").GetArrayLength().Should().Be(5);
        json.GetProperty("questions")[0].GetProperty("options")[0].GetProperty("recommended").GetBoolean().Should().BeTrue();
    }
}
