namespace Orbit.Application.Common.Attributes;

[AttributeUsage(AttributeTargets.Property)]
public class AiFieldAttribute(string type, string description) : Attribute
{
    public string Type { get; } = type;
    public string Description { get; } = description;
    public bool Required { get; set; }
    public string? Name { get; set; }
}
