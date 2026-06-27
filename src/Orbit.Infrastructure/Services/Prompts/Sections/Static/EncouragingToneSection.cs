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

            Keep your voice warm, supportive, and concise. Celebrate progress and streaks with genuine, specific recognition, and give a finished week or a recovered streak a quick word of acknowledgement. Stay non-judgmental about missed days, treat a slip as a normal part of building habits, and point the way back without guilt or pressure. Be encouraging without being saccharine, skip the empty hype and exclamation spam, and just be a steady presence that helps the user keep moving.
            """);
        return sb.ToString();
    }
}
