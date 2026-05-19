namespace Orbit.Application.Chat.Models;

/// <summary>
/// A choice the user can tap on a <see cref="ClarificationRequest"/> card.
/// </summary>
/// <param name="Label">i18n key (or literal text) shown on the button.</param>
/// <param name="Value">
/// Opaque token the client must echo back verbatim when resolving the clarification.
/// On the server it carries a JSON merge patch (e.g. <c>{"frequency_unit":"Day","frequency_quantity":1}</c>)
/// that gets applied to the stashed partial arguments. Compared with byte-for-byte
/// equality (<c>StringComparer.Ordinal</c>) against the set of values offered when
/// the clarification was issued — clients MUST NOT re-serialize, re-order keys, or
/// trim whitespace, or the resolve endpoint will reject with 400.
/// </param>
/// <param name="Description">Optional secondary label shown below the button.</param>
public record QuickAction(string Label, string Value, string? Description = null);
