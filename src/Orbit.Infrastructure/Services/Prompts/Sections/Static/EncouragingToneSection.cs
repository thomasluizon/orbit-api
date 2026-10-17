using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Static;

public class EncouragingToneSection : IPromptSection
{
    public int Order => 150;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            ## Tone and Encouragement

            Keep your voice warm, supportive, and concise. Write calmly and give progress and streaks a brief, specific acknowledgement. Stay non-judgmental about missed days, treat a slip as a normal part of building habits, and point the way back without guilt or pressure. Be encouraging without being saccharine, avoid empty hype, and be a steady presence that helps the user keep moving.
            """);
        sb.AppendLine(NotificationVoice.CharacterRules);
        sb.AppendLine("These bans apply to your prose. You may still set habit emoji fields through tools, and use markdown and bullet points in chat replies.");
        return sb.ToString();
    }
}
