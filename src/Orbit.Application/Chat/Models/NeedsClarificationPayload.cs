namespace Orbit.Application.Chat.Models;

/// <summary>
/// Internal payload returned by a tool that wants to ask the user a question instead
/// of executing. The chat handler stashes this server-side, mints an <c>OperationId</c>
/// from the store, and constructs the external-facing <see cref="ClarificationRequest"/>
/// before surfacing it to the frontend.
/// <para>
/// Tools should not attempt to populate an <c>OperationId</c> themselves — the handler
/// owns that field. Keeping the tool's output free of the id avoids a <c>Guid.Empty</c>
/// sentinel leaking into the contract.
/// </para>
/// </summary>
public record NeedsClarificationPayload(
    string Question,
    string MissingArgumentKey,
    IReadOnlyList<QuickAction>? QuickActions);
