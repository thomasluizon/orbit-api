using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.ML.Tokenizers;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Tests.AI;

public sealed class O200kTokenCounterTests
{
    private static readonly TiktokenTokenizer Reference = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static TheoryData<string> Texts => new()
    {
        string.Empty,
        "a",
        "Hello world, this is a test of the tokenizer.",
        "Olá, tudo bem? Organize minha rotina, café e leitura.",
        "I'm sure they'll say we've done it; it's THE END'S start.",
        "   leading and trailing spaces   ",
        "tabs\tand\r\nnew\n\n\nlines\r\r\n  \n",
        "numbers 1234567890 3.14159 -42 1e10",
        "emoji 🧘‍♀️🏃🏽 and 日本語のテキスト and Ελληνικά and العربية",
        "code: if (x != null) { return x?.Value ?? 0; } // done",
        "<|endoftext|>",
        "before<|endoftext|>after <|endofprompt|>  spaces",
        "<|endoftext|> <|endofprompt|><|endoftext|>",
        "a lone surrogate \ud800 here",
        new string('x', 5000),
        string.Concat(Enumerable.Repeat("supercalifragilistic", 300)),
        """{"model":"gpt-6-luna","input":[{"role":"user","content":"Rank my habits"}],"max_output_tokens":4096}"""
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void CountTokens_MatchesTheLibraryTokenizer(string text)
    {
        O200kTokenCounter.Shared.CountTokens(text).Should().Be(Reference.CountTokens(text));
    }

    [Fact]
    public async Task CountTokens_MatchesTheLibraryOnEveryRecordedProductionRequest()
    {
        var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiReservation"), "*.json.gz");
        fixtures.Should().NotBeEmpty();
        var compared = 0;
        foreach (var file in fixtures.Order(StringComparer.Ordinal))
        {
            await using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            using var fixture = await JsonDocument.ParseAsync(stream);
            foreach (var text in Strings(fixture.RootElement.GetProperty("request")).Prepend(fixture.RootElement.GetProperty("request").GetRawText()))
            {
                O200kTokenCounter.Shared.CountTokens(text).Should().Be(Reference.CountTokens(text), Path.GetFileName(file));
                compared++;
            }
        }
        compared.Should().BeGreaterThan(fixtures.Length);
    }

    [Fact]
    public void CountTokens_MatchesTheLibraryOnRandomUnicodeText()
    {
        var random = new Random(2003);
        var alphabet = "aZ09 '\t\r\n.,;:!?-_/\\\"()[]{}<|>éçãß漢字ж😀́".EnumerateRunes().ToArray();
        for (var sample = 0; sample < 500; sample++)
        {
            var text = new StringBuilder();
            for (var index = random.Next(1, 200); index > 0; index--)
                text.Append(alphabet[random.Next(alphabet.Length)].ToString());
            var value = text.ToString();
            O200kTokenCounter.Shared.CountTokens(value).Should().Be(Reference.CountTokens(value), value);
        }
    }

    [Fact]
    public void Vocabulary_HoldsEveryRankInFlatArraysOfAFewMegabytes()
    {
        var counter = O200kTokenCounter.Shared;

        counter.TokenCount.Should().Be(199_998);
        counter.RetainedBytes.Should().BeLessThan(8L * 1024 * 1024,
            "the library tokenizer retains about 42 MB of managed memory for the same vocabulary");
        counter.Rank("Capacity"u8).Should().Be(Reference.EncodeToIds("Capacity").Single());
    }

    [Fact]
    public void Infrastructure_NeverReferencesTheLibraryTokenizer()
    {
        typeof(O200kTokenCounter).Assembly.GetReferencedAssemblies().Select(name => name.Name)
            .Should().NotContain("Microsoft.ML.Tokenizers",
                "the library tokenizer caches about 42 MB statically; O200kTokenCounter counts with about 4 MB");
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value).Prepend(property.Name)),
        _ => []
    };
}
