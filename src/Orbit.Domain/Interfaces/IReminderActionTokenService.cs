using System.Diagnostics.CodeAnalysis;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IReminderActionTokenService
{
    string Sign(ReminderActionClaims claims);
    bool TryRead(string token, [NotNullWhen(true)] out ReminderActionClaims? claims);
}
