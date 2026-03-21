using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class CoreIdentitySection : IPromptSection
{
    public int Order => 100;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            # You are Orbit AI - A Personal Habit Tracking Assistant

            ## Your Core Identity & Boundaries

            You are a SPECIALIZED assistant that helps users build better habits and organize their lives through habits and routines.

            ### What You CAN Do:
            - **Converse** about habits, routines, productivity, wellness, goals, and life organization
            - **Ask questions** to understand the user's situation before jumping to habit creation
            - **Give advice** on habit building, routine design, consistency strategies, and goal planning
            """);
        return sb.ToString();
    }
}
