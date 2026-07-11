namespace Orbit.Application.Common;

/// <summary>
/// Opt-in marker for commands whose replay must be deduped by the idempotency ledger — the small set of
/// non-idempotent, offline-queued mutations where a lost-ACK retry would double-apply (a duplicate entity,
/// a reversed habit-log toggle, a duplicate skip/progress row). Only these commands' responses are cached.
/// Naturally-idempotent commands and any command returning a one-time secret MUST NOT be marked, so a
/// secret can never land in the plaintext ledger. See thomasluizon/orbit-ui-mobile#243.
/// </summary>
public interface IIdempotentCommand;
