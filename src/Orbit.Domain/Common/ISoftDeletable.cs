namespace Orbit.Domain.Common;

public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTime? DeletedAtUtc { get; }
    void SoftDelete();
}
