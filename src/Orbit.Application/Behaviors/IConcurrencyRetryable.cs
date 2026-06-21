namespace Orbit.Application.Behaviors;

/// <summary>
/// Marks a MediatR request whose handler is safe to re-run from scratch when its save hits an
/// optimistic-concurrency conflict (the Postgres <c>xmin</c> token on User/Goal/Referral). The
/// whole handler re-executes on retry, so it must be idempotent and free of non-idempotent
/// external side effects (e.g. creating a Stripe coupon). Pure-DB and idempotent-read handlers
/// qualify; handlers that perform external writes must resolve concurrency themselves instead.
/// </summary>
public interface IConcurrencyRetryable;
