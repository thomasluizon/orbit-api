using System.Text;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class UserFactsSection : IPromptSection
{
    private static readonly (string Key, string Header)[] FactCategories =
    [
        ("preference", "**Preferences** (likes, dislikes, personal style):"),
        ("routine", "**Routines** (schedules, patterns, recurring behaviors):"),
        ("context", "**Context** (goals, life situation, background):"),
        ("general", "**Other:**")
    ];

    public int Order => 500;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## What You Know About This User");

        if (context.UserFacts.Count == 0)
        {
            sb.AppendLine("(nothing yet - learn as you go)");
        }
        else
        {
            var grouped = context.UserFacts
                .OrderByDescending(f => f.ExtractedAtUtc)
                .GroupBy(f => f.Category?.ToLowerInvariant() ?? "general")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (key, header) in FactCategories)
            {
                AppendFactCategory(sb, grouped, key, header);
            }
        }

        sb.AppendLine();
        sb.AppendLine("""
            ### How to Use These Facts:
            - **Preferences**: Tailor habit suggestions to what the user enjoys. If they prefer outdoors, suggest outdoor activities over gym workouts. If they dislike mornings, don't suggest 6am habits.
            - **Routines**: Avoid scheduling conflicts. If the user works night shifts, don't suggest late-night habits. Use known patterns to suggest realistic times and frequencies.
            - **Context**: Align suggestions with the user's goals. If they're training for a marathon, running-related habits get priority. If they're a student, study habits are relevant.
            - NEVER parrot facts back unprompted ("Since you work from home..."). Use them silently to shape better responses.
            - When facts conflict with a user's request, gently acknowledge it (e.g., user says "I want to wake up at 5am" but facts say they work night shifts - ask if they're sure).
            """);
        return sb.ToString();
    }

    private static void AppendFactCategory(
        StringBuilder sb, Dictionary<string, List<UserFact>> grouped,
        string key, string header)
    {
        if (!grouped.TryGetValue(key, out var facts) || facts.Count == 0)
            return;

        sb.AppendLine(header);
        foreach (var fact in facts)
            sb.AppendLine($"  - {fact.FactText}");
    }
}
