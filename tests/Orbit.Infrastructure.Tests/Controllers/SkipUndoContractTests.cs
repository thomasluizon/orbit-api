using System.Text.Json;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class SkipUndoContractTests
{
    [Fact]
    public void GeneratedOpenApi_AppendsUndoAndOptionalSkipId_KeepingSkipNoContent()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api/openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var paths = document.RootElement.GetProperty("paths");
        paths.GetProperty("/api/Habits/{id}/skip").GetProperty("post")
            .GetProperty("responses").TryGetProperty("204", out _).Should().BeTrue();
        var undo = paths.GetProperty("/api/Habits/{id}/skip/{skipId}/undo").GetProperty("post");
        var responses = undo.GetProperty("responses");
        responses.TryGetProperty("204", out _).Should().BeTrue();
        responses.TryGetProperty("404", out _).Should().BeTrue();
        responses.TryGetProperty("409", out _).Should().BeTrue();
        undo.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString())
            .Should().BeEquivalentTo("id", "skipId");
        var skip = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("SkipHabitRequest");
        skip.GetProperty("properties").GetProperty("skipId").GetProperty("type").EnumerateArray()
            .Select(type => type.GetString()).Should().BeEquivalentTo("string", "null");
        if (skip.TryGetProperty("required", out var required))
            required.EnumerateArray().Select(field => field.GetString()).Should().NotContain("skipId");
    }
}
