using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class ChecklistTemplate : Entity, ITimestamped, ISoftDeletable
{
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = null!;
    public IReadOnlyList<string> Items { get; private set; } = [];
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    private ChecklistTemplate() { }

    public static Result<ChecklistTemplate> Create(Guid userId, string name, IReadOnlyList<string> items)
    {
        if (userId == Guid.Empty)
            return Result.Failure<ChecklistTemplate>(DomainErrors.UserIdRequired);

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<ChecklistTemplate>(DomainErrors.TemplateNameRequired);

        if (name.Trim().Length > 100)
            return Result.Failure<ChecklistTemplate>(DomainErrors.TemplateNameTooLong);

        var itemsCheck = ValidateItems(items);
        if (itemsCheck.IsFailure)
            return Result.Failure<ChecklistTemplate>(itemsCheck.Error, itemsCheck.ErrorCode!);

        return Result.Success(new ChecklistTemplate
        {
            UserId = userId,
            Name = name.Trim(),
            Items = items.Select(i => i.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToList(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    public Result Update(string name, IReadOnlyList<string> items)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(DomainErrors.TemplateNameRequired);

        if (name.Trim().Length > 100)
            return Result.Failure(DomainErrors.TemplateNameTooLong);

        var itemsCheck = ValidateItems(items);
        if (itemsCheck.IsFailure)
            return itemsCheck;

        Name = name.Trim();
        Items = items.Select(i => i.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToList();
        UpdatedAtUtc = DateTime.UtcNow;
        return Result.Success();
    }

    private static Result ValidateItems(IReadOnlyList<string> items)
    {
        if (items.Count == 0 || items.All(string.IsNullOrWhiteSpace))
            return Result.Failure(DomainErrors.TemplateItemsRequired);
        if (items.Count > DomainConstants.MaxChecklistItems)
            return Result.Failure("Too many checklist items.", "TEMPLATE_ITEM_LIMIT");
        if (items.Any(item => item.Length > DomainConstants.MaxChecklistItemTextLength))
            return Result.Failure("Checklist item text is too long.", "TEMPLATE_ITEM_LENGTH");
        return Result.Success();
    }

    public void SoftDelete()
    {
        IsDeleted = true;
        DeletedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
