namespace Orbit.Domain.Common;

public interface ITimestamped
{
    DateTime UpdatedAtUtc { get; set; }
}
