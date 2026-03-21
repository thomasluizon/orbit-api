namespace Orbit.Infrastructure.Services.Prompts;

public interface IPromptSection
{
    int Order { get; }
    bool ShouldInclude(PromptContext context);
    string Build(PromptContext context);
}
