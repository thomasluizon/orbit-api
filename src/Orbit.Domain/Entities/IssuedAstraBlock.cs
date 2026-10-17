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
        ValidateDefinition(definition);

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

    private static void ValidateDefinition(AstraBlockDefinition definition)
    {
        var valid = definition.Form switch
        {
            AstraBlockKinds.Choice => IsValidChoice(definition),
            AstraBlockKinds.PickList => IsValidPickList(definition),
            _ => false
        };
        if (!valid)
            throw new ArgumentException("Invalid block definition.", nameof(definition));
    }

    private static bool IsValidChoice(AstraBlockDefinition definition) =>
        definition.Title.Length <= 120 && definition.Options.Count is >= 2 and <= 4
        && definition.Items.Count == 0 && definition.Action is null
        && definition.Options.All(option => option is not null && !string.IsNullOrWhiteSpace(option.Id)
            && !string.IsNullOrWhiteSpace(option.Label) && option.Label.Length <= 40
            && (option.Description is null || option.Description.Length <= 80))
        && definition.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() == definition.Options.Count;

    private static bool IsValidPickList(AstraBlockDefinition definition) =>
        definition.Items.Count is >= 1 and <= 10 && definition.Options.Count == 0
        && definition.Action is "create_habits" or "log_habits" or "skip_habits"
        && definition.MultiSelect && !definition.AllowFreeText
        && definition.Items.All(item => item is not null && !string.IsNullOrWhiteSpace(item.Id)
            && !string.IsNullOrWhiteSpace(item.Label) && item.Arguments.ValueKind == JsonValueKind.Object)
        && definition.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == definition.Items.Count;

    public void MarkAnswered(DateTime answeredAtUtc)
    {
        if (AnsweredAtUtc.HasValue || InvalidatedAtUtc.HasValue || answeredAtUtc >= ExpiresAtUtc
            || answeredAtUtc < CreatedAtUtc)
            throw new InvalidOperationException("Block is not open.");
        AnsweredAtUtc = answeredAtUtc;
    }
}
