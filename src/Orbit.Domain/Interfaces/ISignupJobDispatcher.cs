namespace Orbit.Domain.Interfaces;

public interface ISignupJobDispatcher
{
    void Dispatch(Guid userId, bool signupPending, string language, string? referralCode);
}
