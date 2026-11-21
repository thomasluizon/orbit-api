using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.ML.Tokenizers;
using Orbit.Application.Chat.References;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// The habit index line at the chat boundary (thomasluizon/orbit-tickets#2154, acceptance check 9). The prompt section
/// writes full GUIDs, and the boundary writes the model copy with a short reference for each listed record.
/// </summary>
public sealed partial class ShortReferenceIndexTokenTests(ITestOutputHelper output)
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>
    /// One index line of a habit with a parent and one tag, before and after the boundary, and an index of 100 such lines.
    /// Each line has fresh GUIDs, because the tokens of a GUID depend on its digits.
    /// </summary>
    [Fact]
    public async Task IndexLineWithAParentAndATag_ShowsThreeReferencesOfEightDigitsAndCostsFewerTokens()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var (fullTotal, referencedTotal) = (0, 0);
        for (var line = 0; line < 100; line++)
        {
            var (fullLine, referencedLine) = await IndexLineAsync();
            fullTotal += tokenizer.CountTokens(fullLine);
            referencedTotal += tokenizer.CountTokens(referencedLine);
            if (line == 0)
            {
                output.WriteLine($"AI:ShortReferences off: {tokenizer.CountTokens(fullLine)} tokens: {fullLine}");
                output.WriteLine($"AI:ShortReferences on: {tokenizer.CountTokens(referencedLine)} tokens: {referencedLine}");
            }
        }
        output.WriteLine($"Index of 100 lines, o200k_base: off {fullTotal} tokens, on {referencedTotal} tokens.");
        referencedTotal.Should().BeLessThan(fullTotal);
    }

    private static async Task<(string Full, string Referenced)> IndexLineAsync()
    {
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Morning routine", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        var child = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId, "Drink water", FrequencyUnit.Day, 1,
            DueDate: Today, ParentHabitId: parent.Id, Emoji: "💧")).Value;
        var tag = Tag.Create(UserId, "Health", "#22c55e").Value;
        child.AddTag(tag);
        var prompt = new ActiveHabitsSection().Build(new PromptContext([parent, child], false, null, null, Today, null));
        var fullLine = prompt.Split('\n').Single(line => line.Contains("\"Drink water\"", StringComparison.Ordinal)).TrimEnd('\r');
        using var book = new ReferenceBook((_, _) => Task.FromResult<IReadOnlyList<ReferenceRecord>>([]));
        book.Seed(ReferenceRecordType.Habit, [new(parent.Id, parent.Title), new(child.Id, child.Title)]);
        book.Seed(ReferenceRecordType.Tag, [new(tag.Id, tag.Name)]);
        var referencedLine = await book.WriteAsync(fullLine, default);
        foreach (var id in new[] { child.Id, parent.Id, tag.Id })
        {
            fullLine.Should().Contain(id.ToString());
            referencedLine.Should().NotContain(id.ToString()).And.Contain(ReferenceCodes.Hex(id)[..8]);
        }
        GuidText().IsMatch(referencedLine).Should().BeFalse("each GUID of the line is a habit or a tag");
        return (fullLine, referencedLine);
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidText();
}
