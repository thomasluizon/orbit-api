namespace Orbit.Application.Common.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class AiActionAttribute(string actionType, string capability, string whenToUse) : Attribute
{
    public string ActionType { get; } = actionType;
    public string Capability { get; } = capability;
    public string WhenToUse { get; } = whenToUse;
    public int DisplayOrder { get; set; } = 100;
}
