using MediatR;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class SetProactiveAstraTool(IMediator mediator) : ToggleProfileSettingTool(mediator)
{
    public override string Name => "set_proactive_astra";
    public override string Description => "Enable or disable proactive Astra push check-ins when the user asks. Requires Pro; in-app proactive items remain available.";
    protected override string SettingLabel => "Proactive Astra";

    protected override IRequest<Result> CreateCommand(Guid userId, bool enabled) =>
        new SetProactiveAstraEnabledCommand(userId, enabled);
}
