namespace Orbit.Application.Common.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class AiExampleAttribute(string userMessage, string jsonResponse) : Attribute
{
    public string UserMessage { get; } = userMessage;
    public string JsonResponse { get; } = jsonResponse;
    public string? Note { get; set; }
}
