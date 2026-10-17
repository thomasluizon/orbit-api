using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Entities;

public sealed class IssuedAstraBlock : Entity
{
    public Guid UserId { get; private set; }
    public string Form { get; private set; } = string.Empty;
    public string DefinitionJson { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? AnsweredAtUtc { get; private set; }
    public DateTime? InvalidatedAtUtc { get; private set; }

    private IssuedAstraBlock() { }

    public AstraBlockDefinition ReadDefinition() =>
        JsonSerializer.Deserialize<AstraBlockDefinition>(DefinitionJson)
            ?? throw new InvalidOperationException("Stored block has no definition.");

    public static IssuedAstraBlock Create(Guid userId, AstraBlockDefinition definition)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Title))
            throw new ArgumentException("A question or title is required.", nameof(definition));
        if (definition.Form == AstraBlockKinds.Choice)
        {
            if (definition.Title.Length > 120 || definition.Options.Count is < 2 or > 4
                || definition.Items.Count != 0 || definition.Action is not null
                || definition.Options.Any(option => string.IsNullOrWhiteSpace(option.Id)
                    || string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 40
                    || option.Description?.Length > 80)
                || definition.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != definition.Options.Count)
                throw new ArgumentException("Invalid choice definition.", nameof(definition));
        }
        else if (definition.Form == AstraBlockKinds.PickList)
        {
            if (definition.Items.Count is < 1 or > 10 || definition.Options.Count != 0
                || definition.Action is not ("create_habits" or "log_habits" or "skip_habits")
                || !definition.MultiSelect || definition.AllowFreeText
                || definition.Items.Any(item => string.IsNullOrWhiteSpace(item.Id)
                    || string.IsNullOrWhiteSpace(item.Label) || item.Arguments.ValueKind != JsonValueKind.Object)
                || definition.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != definition.Items.Count)
                throw new ArgumentException("Invalid pick list definition.", nameof(definition));
        }
        else
            throw new ArgumentException("Unknown block form.", nameof(definition));

        var createdAtUtc = DateTime.UtcNow;
        return new IssuedAstraBlock
        {
            UserId = userId,
            Form = definition.Form,
            DefinitionJson = JsonSerializer.Serialize(definition),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddHours(24)
        };
    }

    public void MarkAnswered(DateTime answeredAtUtc)
    {
        if (AnsweredAtUtc.HasValue || InvalidatedAtUtc.HasValue || answeredAtUtc >= ExpiresAtUtc
            || answeredAtUtc < CreatedAtUtc)
            throw new InvalidOperationException("Block is not open.");
        AnsweredAtUtc = answeredAtUtc;
    }
}
