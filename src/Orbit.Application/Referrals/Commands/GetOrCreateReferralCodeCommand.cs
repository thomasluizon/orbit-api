using System.Security.Cryptography;
using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Commands;

public record GetOrCreateReferralCodeCommand(Guid UserId) : IRequest<Result<string>>;

public class GetOrCreateReferralCodeCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<GetOrCreateReferralCodeCommand, Result<string>>
{
    private const string AllowedChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;

    public async Task<Result<string>> Handle(GetOrCreateReferralCodeCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<string>("User not found.");

        if (user.ReferralCode is not null)
            return Result.Success(user.ReferralCode);

        // Generate unique code
        string code;
        do
        {
            code = GenerateCode();
            var existing = await userRepository.FindAsync(
                u => u.ReferralCode == code,
                cancellationToken: cancellationToken);

            if (existing.Count == 0)
                break;
        } while (true);

        user.SetReferralCode(code);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(code);
    }

    private static string GenerateCode()
    {
        return string.Create(CodeLength, AllowedChars, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[bytes[i] % chars.Length];
        });
    }
}
