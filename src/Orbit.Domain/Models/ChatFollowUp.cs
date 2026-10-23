namespace Orbit.Domain.Models;

public sealed record ChatFollowUp
{
    public string Label { get; }
    public string Question { get; }
    public string? Context { get; }
    public string? Target { get; }
    public Guid? QueryId { get; }

    public ChatFollowUp(string label, string question, string? context = null, string? target = null, Guid? queryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        if (label.Length > 40 || question.Length > 240 || context?.Length > 500
            || label.IndexOfAny(['\r', '\n']) >= 0 || question.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Follow-up text exceeds its limits.");
        if (target is not null && !IsTarget(target) || queryId == Guid.Empty)
            throw new ArgumentException("Follow-up context requires a supported target and query.");
        Label = label;
        Question = question;
        Context = context;
        Target = target;
        QueryId = queryId;
    }

    public static bool IsTarget(string target) => target is "habits" or "goals" or "progress" or "today"
        or "calendar" or "notifications" or "tags" or "checklistTemplates" or "apiKeys" or "profile";
}
