namespace Orbit.Application.Chat.Tools;

public class AiToolRegistry
{
    private readonly Dictionary<string, IAiTool> _tools;

    public AiToolRegistry(IEnumerable<IAiTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IAiTool? GetTool(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<IAiTool> GetAll() => _tools.Values.ToList();
}
