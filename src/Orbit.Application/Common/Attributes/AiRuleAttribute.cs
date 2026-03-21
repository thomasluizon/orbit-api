namespace Orbit.Application.Common.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class AiRuleAttribute(string rule) : Attribute
{
    public string Rule { get; } = rule;
}
