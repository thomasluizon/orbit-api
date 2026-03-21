using System.Text;

namespace Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;

public class UserTagsSection : IPromptSection
{
    public int Order => 400;
    public bool ShouldInclude(PromptContext context) => true;

    public string Build(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## User's Tags");
        if (context.UserTags is { Count: > 0 })
        {
            foreach (var tag in context.UserTags)
                sb.AppendLine($"- \"{tag.Name}\" (color: {tag.Color})");
        }
        else
        {
            sb.AppendLine("(no tags created yet)");
        }
        return sb.ToString();
    }
}
