using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IReadQueryTokenService
{
    string Issue(ReadQueryTicket ticket);
    ReadQueryTicket? Read(string token, Guid userId);
}
