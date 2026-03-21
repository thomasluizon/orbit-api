using System.Reflection;
using Orbit.Application.Common.Attributes;

namespace Orbit.Infrastructure.Services.Prompts;

public class ActionDiscoveryService
{
    private readonly IReadOnlyList<DiscoveredAction> _actions;

    public ActionDiscoveryService()
    {
        _actions = typeof(AiActionAttribute).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<AiActionAttribute>() != null)
            .Select(t =>
            {
                var metadata = t.GetCustomAttribute<AiActionAttribute>()!;
                var rules = t.GetCustomAttributes<AiRuleAttribute>().Select(r => r.Rule).ToList();
                var examples = t.GetCustomAttributes<AiExampleAttribute>().ToList();
                var fields = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p =>
                    {
                        var attr = p.GetCustomAttribute<AiFieldAttribute>();
                        if (attr is null) return null;
                        var name = attr.Name ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                        return new DiscoveredField(name, attr);
                    })
                    .Where(f => f is not null)
                    .Cast<DiscoveredField>()
                    .ToList();

                return new DiscoveredAction(metadata, rules, examples, fields);
            })
            .OrderBy(a => a.Metadata.DisplayOrder)
            .ToList();

        Validate();
    }

    public IReadOnlyList<DiscoveredAction> GetActions() => _actions;

    private void Validate()
    {
        var duplicates = _actions
            .GroupBy(a => a.Metadata.ActionType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate AI action types: {string.Join(", ", duplicates)}");

        var noExamples = _actions
            .Where(a => a.Examples.Count == 0)
            .Select(a => a.Metadata.ActionType)
            .ToList();

        if (noExamples.Count > 0)
            throw new InvalidOperationException(
                $"AI actions missing examples: {string.Join(", ", noExamples)}");
    }
}
