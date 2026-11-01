#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Configuration;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Domain.Enums;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiInputBudgetTests
{
    private static readonly JsonSerializerOptions TokenJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly TiktokenTokenizer CacheTokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    [Theory]
    [InlineData("legacy")]
    [InlineData("composition")]
    [InlineData("offers")]
    public async Task CacheRequests_KeepFullStaticPrefixAcrossUsersAndRounds(string client)
    {
        var captures = await CaptureCacheTurnsAsync(client);
        var first = captures[0];
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"orbit-1916-request-{client}.json"), first.Body);
        foreach (var capture in captures)
            StaticPrefix(capture.Body).Should().Be(StaticPrefix(first.Body), capture.Name);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("composition")]
    [InlineData("offers")]
    public async Task CacheRequests_ToolLoopOnlyAppendsInput(string client)
    {
        var captures = await CaptureCacheTurnsAsync(client);
        var loop = captures.Where(capture => capture.Turn == "loop").ToArray();
        for (var index = 1; index < loop.Length; index++)
        {
            ModelInput(loop[index].Body).Should().StartWith(ModelInput(loop[index - 1].Body));
            var previous = InputItems(loop[index - 1].Body);
            var current = InputItems(loop[index].Body);
            current.Length.Should().BeGreaterThan(previous.Length);
            current.Take(previous.Length).Should().Equal(previous, loop[index].Name);
        }
    }

    [Fact]
    public async Task CacheRequests_HistoryGrowsWithoutRewritingEarlierItems()
    {
        var user = User.Create("History person", "history@example.com").Value;
        ChatHistoryMessage[] history = [new("user", "Show my progress"), new("assistant", "Here is your progress.")];
        var first = await BuildCacheRequestAsync("offers", user, history);
        var next = await BuildCacheRequestAsync("offers", user, [.. history, new("user", "And my goals?"), new("assistant", "Here are your goals.")]);
        var transport = new SequenceHandler(ResponsesTestData.Text("Done"));
        var service = BuildService(transport);
        (await service.SendWithToolsAsync(first)).IsSuccess.Should().BeTrue();
        (await service.SendWithToolsAsync(next)).IsSuccess.Should().BeTrue();
        var earlier = InputItems(transport.Bodies[0]).Skip(1).Take(history.Length).ToArray();
        InputItems(transport.Bodies[1]).Skip(1).Take(earlier.Length).Should().Equal(earlier);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("composition")]
    [InlineData("offers")]
    public async Task CacheRequests_RepresentativeWarmPrefixMeanCostsAtMostNineTenThousandths(string client)
    {
        var captures = await CaptureCacheTurnsAsync(client);
        var seen = new List<string> { CostStaticPrefix(captures[^1].Body) };
        var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build()
            .GetSection("AI").Get<AiSettings>()!;
        var price = AiRequestCostEstimator.GetPrice(settings, "gpt-6-luna");
        price.InputPerMillionUsd.Should().Be(0.10m);
        price.CachedInputPerMillionUsd.Should().Be(0.01m);
        price.CacheWritePerMillionUsd.Should().Be(0.125m);
        price.OutputPerMillionUsd.Should().Be(0.50m);
        var coldSeen = new List<string>();
        var coldCosts = new List<decimal>();
        var costs = new List<decimal>();
        var table = new StringBuilder("| Call | System | Tools | Context | History | User | Calls | Results | Offers | Other | Prefix in turn B/T | Prefix previous turn B/T | Prefix other user B/T | Cached | Warm cost USD | Cold cost USD |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");
        string? previous = null;
        string? previousTurn = null;
        string? turn = null;
        var otherUser = captures.Single(capture => capture.Turn == "other-user").Body;
        foreach (var capture in captures)
        {
            var prefix = ModelInput(capture.Body);
            if (capture.Turn != turn)
            {
                previousTurn = previous;
                previous = null;
                turn = capture.Turn;
            }
            var shared = seen.Count == 0 ? 0 : seen.Max(earlier => CommonTokens(earlier, prefix));
            var cached = shared >= 1024 ? shared / 128 * 128 : 0;
            var counts = SectionTokens(capture.Body);
            var total = Count(prefix);
            var coldShared = coldSeen.Count == 0 ? 0 : coldSeen.Max(earlier => CommonTokens(earlier, prefix));
            var coldCached = coldShared >= 1024 ? coldShared / 128 * 128 : 0;
            var coldCost = ((total - coldCached) * price.CacheWritePerMillionUsd!.Value + coldCached * price.CachedInputPerMillionUsd + capture.OutputTokens * price.OutputPerMillionUsd) / 1_000_000;
            coldCosts.Add(coldCost);
            coldSeen.Add(prefix);
            var cost = ((total - cached) * price.CacheWritePerMillionUsd!.Value + cached * price.CachedInputPerMillionUsd + capture.OutputTokens * price.OutputPerMillionUsd) / 1_000_000;
            costs.Add(cost);
            table.AppendLine(FormattableString.Invariant($"| {client}/{capture.Name} | {string.Join(" | ", counts)} | {SharedPrefix(previous is null ? null : ModelInput(previous), prefix)} | {SharedPrefix(previousTurn is null ? null : ModelInput(previousTurn), prefix)} | {SharedPrefix(ModelInput(capture.Turn == "other-user" ? captures[0].Body : otherUser), prefix)} | {cached} | {cost:F7} | {coldCost:F7} |"));
            seen.Add(prefix);
            previous = capture.Body;
        }
        table.AppendLine(FormattableString.Invariant($"D147 mean with previously sent static prefix: ${costs.Average():F7}; mean including cold start: ${coldCosts.Average():F7}"));
        output.WriteLine(table.ToString());
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), $"orbit-1916-cache-{client}.md"), table.ToString());
        costs.Average().Should().BeLessThanOrEqualTo(0.0009m);
        var (firstCallLimit, coldMeanLimit) = client switch
        {
            "legacy" => (0.0054m, 0.00132m),
            "composition" => (0.0061m, 0.00147m),
            _ => (0.0068m, 0.00162m)
        };
        coldCosts[0].Should().BeLessThanOrEqualTo(firstCallLimit, "a session's first call must fit the compact catalog budget");
        coldCosts.Average().Should().BeLessThanOrEqualTo(coldMeanLimit);
    }

    private static async Task<List<CacheCapture>> CaptureCacheTurnsAsync(string client)
    {
        var read = TerminalReadResponse();
        var write = CapturedWriteResponse();
        var captures = new List<CacheCapture>();
        var user = User.Create("Cache person", "cache@example.com").Value;
        var other = User.Create("Outra pessoa", "cache-other@example.com").Value;
        other.SetLanguage("pt-BR");
        var habits = new Dictionary<Guid, IReadOnlyList<Habit>>();
        var fixtureIds = new[] { "e2f7193a-57a4-4ae8-bb19-9bc12d3a408f", "6011da4b-a92e-4428-a91a-c728bf0a720c",
            "c2404d6f-cb03-49c2-8840-57206a0b70d9", "3c22af14-4a13-4e57-9ce1-08be553547e4" };
        var identity = 0;
        foreach (var person in new[] { user, other })
            habits[person.Id] = new[] { "Stretch", "Read" }.Select(title =>
            {
                var habit = Habit.Create(new(HabitSchedule.Recurring, person.Id, title, FrequencyUnit.Day, 1, new DateOnly(2026, 1, 1))).Value;
                typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(habit, Guid.Parse(fixtureIds[identity++]));
                return habit;
            }).ToArray();
        var history = new[] { new ChatHistoryMessage("user", "Show my progress"), new ChatHistoryMessage("assistant", "Here is your progress.") };
        foreach (var (turn, person, prior) in new[] { ("read", user, (ChatHistoryMessage[]?)null), ("write", user, null),
            ("loop", user, null), ("followup", user, history), ("other-user", other, null) })
        {
            var request = await BuildCacheRequestAsync(client, person, prior, habits[person.Id],
                turn is "write" or "loop" ? CapturedWriteMessage() : "Show my habit ranking over the last six months.",
                reverseKinds: person == other);
            var responses = turn switch
            {
                "write" => new[] { ModelReaderWriter.Write(write).ToString() },
                "loop" => new[] { read, ModelReaderWriter.Write(write).ToString(), ResponsesTestData.Text("Ready for your review.") },
                _ => new[] { read }
            };
            var transport = new SequenceHandler(responses);
            var service = BuildService(transport);
            var result = await service.SendWithToolsAsync(request);
            result.IsSuccess.Should().BeTrue(result.Error);
            if (turn == "loop")
            {
                var call = result.Value.ToolCalls!.Single();
                var conversation = result.Value.ConversationContext!;
                conversation.ToolDeclarationUpdates = client == "legacy" ? [] : AskConstraints();
                result = await service.ContinueWithToolResultsAsync(conversation,
                    [new(call.Name, call.Id, true, null, null, null, new { items = new[] { new { id = "owned-habit", title = "Run", completed = 3 } } })]);
                result.IsSuccess.Should().BeTrue(result.Error);
                conversation = result.Value.ConversationContext!;
                var mutation = result.Value.ToolCalls!.Single();
                result = await service.ContinueWithToolResultsAsync(conversation,
                    [new(mutation.Name, mutation.Id, true, null, null, null, new { pending_operation_id = "held-preview", status = "awaiting_confirmation" })]);
                result.IsSuccess.Should().BeTrue(result.Error);
            }
            for (var index = 0; index < transport.Bodies.Count; index++)
            {
                using var response = JsonDocument.Parse(responses[index]);
                var tokens = response.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32();
                captures.Add(new(turn, $"{turn}/{index + 1}", transport.Bodies[index], tokens));
            }
        }
        return captures;
    }

    private static async Task<AiToolRequest> BuildCacheRequestAsync(string client, User user, IReadOnlyList<ChatHistoryMessage>? history,
        IReadOnlyList<Habit>? habits = null, string message = "Show my progress", bool reverseKinds = false)
    {
        var service = Substitute.For<Orbit.Domain.Interfaces.IAiIntentService>();
        AiToolRequest? captured = null;
        service.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => { captured = call.Arg<AiToolRequest>(); return Result.Success(new AiResponse { TextMessage = "Captured" }); });
        var handler = BuildHandler(service, RegisteredTools(), user, habits ?? [], new DateOnly(2026, 1, 1),
            enabledFlags: client == "legacy" ? [] : [FeatureFlagKeys.AstraBlockFirst]);
        var context = client == "legacy" ? null : new AgentClientContext(SupportsParts: true,
            PartKinds: reverseKinds ? ["periodTotal", "periodComparison", "habitList", "question", "pickList", "choice"]
                : ["choice", "pickList", "question", "habitList", "periodComparison", "periodTotal"], SupportsHabitListCard: true,
            SupportsPendingOperationChanges: client == "offers", SupportsProactiveOffers: client == "offers",
            ConversationId: Guid.Parse("11111111-1111-4111-8111-111111111111"));
        var result = await handler.Handle(new(user.Id, message, History: history?.ToList(), ClientContext: context), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        captured.Should().NotBeNull();
        return captured!;
    }

    private static IReadOnlyList<object> AskConstraints()
    {
        var ask = (AskTool)RegisteredTools().Single(tool => tool.Name == "ask");
        return [new { name = ask.Name, description = ask.Description, parameters = ask.GetParameterSchema(true), cacheable_parameters = ask.GetCacheableParameterSchema() }];
    }

    private static string TerminalReadResponse()
    {
        using var stream = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures/TerminalReads/en.json.gz")), CompressionMode.Decompress);
        using var fixture = JsonDocument.Parse(stream);
        return fixture.RootElement.GetProperty("response").GetRawText();
    }

    private static string CapturedWriteMessage()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/BrainDumps/g9a-brain-dump-ideas-en-r3.json")));
        return fixture.RootElement.GetProperty("message").GetString()!;
    }

    private static ResponseResult CapturedWriteResponse()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/BrainDumps/g9a-brain-dump-ideas-en-r3.json")));
        var call = fixture.RootElement.GetProperty("toolCalls").EnumerateArray().Single(call => call.GetProperty("name").GetString() == "bulk_create_habits");
        var response = ResponsesTestData.Response(outputTokens: Count(call.GetProperty("arguments").GetRawText()));
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("captured-write", "bulk_create_habits", BinaryData.FromString(call.GetProperty("arguments").GetRawText())));
        return response;
    }

    private static string[] InputItems(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetRawText()).ToArray();
    }

    private static string StaticPrefix(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("tools").GetRawText() + InputItems(body)[0];
    }

    private static string Canonical(JsonElement value) => JsonSerializer.Serialize(value, TokenJson);

    private static string CostStaticPrefix(string body)
    {
        using var document = JsonDocument.Parse(body);
        return Canonical(document.RootElement.GetProperty("tools")) + Canonical(document.RootElement.GetProperty("input")[0]);
    }

    private static string ModelInput(string body)
    {
        using var document = JsonDocument.Parse(body);
        return Canonical(document.RootElement.GetProperty("tools")) + string.Concat(document.RootElement.GetProperty("input").EnumerateArray().Select(Canonical));
    }

    private static int[] SectionTokens(string body)
    {
        using var document = JsonDocument.Parse(body);
        var input = document.RootElement.GetProperty("input").EnumerateArray().ToArray();
        var sections = new int[9];
        sections[0] = Count(Canonical(input[0]));
        sections[1] = Count(Canonical(document.RootElement.GetProperty("tools")));
        foreach (var item in input.Skip(1))
        {
            var json = Canonical(item);
            var type = item.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            var section = type switch { "function_call" => 5, "function_call_output" => 6, _ => 8 };
            if (item.TryGetProperty("role", out var role))
                section = role.GetString() == "user" ? 4 : json.Contains("conversation_history", StringComparison.Ordinal) ? 3 : 2;
            if (section == 2 && item.TryGetProperty("content", out var content))
            {
                var offerLines = string.Join("\n", content.EnumerateArray().SelectMany(part => part.GetProperty("text").GetString()!.Split('\n'))
                    .Where(line => line.StartsWith("{\"previously_issued_offers\":", StringComparison.Ordinal)
                        || line.StartsWith("{\"offer_account_facts\":", StringComparison.Ordinal)
                        || line.StartsWith("Dismissed offer keys:", StringComparison.Ordinal)));
                var offerTokens = Count(offerLines);
                sections[7] += offerTokens;
                sections[section] -= offerTokens;
            }
            sections[section] += Count(json);
        }
        return sections;
    }

    private static int Count(string value) => CacheTokenizer.CountTokens(value);

    private static int CommonTokens(string? left, string right)
    {
        if (left is null) return 0;
        var leftIds = CacheTokenizer.EncodeToIds(left);
        var rightIds = CacheTokenizer.EncodeToIds(right);
        var length = 0;
        while (length < Math.Min(leftIds.Count, rightIds.Count) && leftIds[length] == rightIds[length]) length++;
        return length;
    }

    private static string SharedPrefix(string? left, string right)
    {
        if (left is null) return "0/0";
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        var length = 0;
        while (length < Math.Min(leftBytes.Length, rightBytes.Length) && leftBytes[length] == rightBytes[length]) length++;
        return $"{length}/{CommonTokens(left, right)}";
    }

    private sealed record CacheCapture(string Turn, string Name, string Body, int OutputTokens);
}
