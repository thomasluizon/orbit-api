using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class UserFactsSection : IPromptSection
{
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

            if (grouped.TryGetValue("preference", out var preferences) && preferences.Count > 0)
            {
                sb.AppendLine("**Preferences** (likes, dislikes, personal style):");
                foreach (var fact in preferences)
                    sb.AppendLine($"  - {fact.FactText}");
            }

            if (grouped.TryGetValue("routine", out var routines) && routines.Count > 0)
            {
                sb.AppendLine("**Routines** (schedules, patterns, recurring behaviors):");
                foreach (var fact in routines)
                    sb.AppendLine($"  - {fact.FactText}");
            }

            if (grouped.TryGetValue("context", out var contexts) && contexts.Count > 0)
            {
                sb.AppendLine("**Context** (goals, life situation, background):");
                foreach (var fact in contexts)
                    sb.AppendLine($"  - {fact.FactText}");
            }

            if (grouped.TryGetValue("general", out var general) && general.Count > 0)
            {
                sb.AppendLine("**Other:**");
                foreach (var fact in general)
                    sb.AppendLine($"  - {fact.FactText}");
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
}
