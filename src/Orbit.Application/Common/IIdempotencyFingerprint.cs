namespace Orbit.Application.Common;

public interface IIdempotencyFingerprint
{
    string IdempotencyFingerprint { get; }
}
