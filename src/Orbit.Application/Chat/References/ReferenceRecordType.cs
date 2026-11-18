namespace Orbit.Application.Chat.References;

/// <summary>
/// The closed list of record types that model text names by a short reference. A GUID of no listed type stays at full
/// length in model text.
/// </summary>
public enum ReferenceRecordType
{
    Habit,
    Goal,
    Tag,
    ChecklistTemplate,
    Notification,
    ApiKey,
    Skip,
    PendingOperation,
    PreviewItem
}

/// <summary>One record of the person that a reference can name, with the title that an <c>unknown_reference</c> error shows.</summary>
public sealed record ReferenceRecord(Guid Id, string Title);

/// <summary>One item of a stored preview: its item id and its title.</summary>
public sealed record PreviewItemReference(string ItemId, string Title);
