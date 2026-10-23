using MediatR;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record StartAppleSignInCommand(string State, string Language = "en", string? ReferralCode = null) : IRequest<string?>;
public record CompleteAppleCallbackCommand(string State, string Code, string? User = null) : IRequest<Result<string>>;
public record TakeAppleHandoffCommand(string Handoff, string State) : IRequest<Result<LoginResponse>>;

public sealed class StartAppleSignInCommandHandler(AppleAuthorizationFlow flow) : IRequestHandler<StartAppleSignInCommand, string?>
{
    public Task<string?> Handle(StartAppleSignInCommand request, CancellationToken cancellationToken) => Task.FromResult(flow.Start(request.State, request.Language, request.ReferralCode));
}

public sealed class CompleteAppleCallbackCommandHandler(AppleAuthorizationFlow flow) : IRequestHandler<CompleteAppleCallbackCommand, Result<string>>
{
    public Task<Result<string>> Handle(CompleteAppleCallbackCommand request, CancellationToken cancellationToken) => flow.CompleteWebCallbackAsync(request.State, request.Code, request.User, cancellationToken);
}

public sealed class TakeAppleHandoffCommandHandler(AppleAuthorizationFlow flow) : IRequestHandler<TakeAppleHandoffCommand, Result<LoginResponse>>
{
    public Task<Result<LoginResponse>> Handle(TakeAppleHandoffCommand request, CancellationToken cancellationToken) => Task.FromResult(flow.TakeHandoff(request.Handoff, request.State));
}
