using FluentAssertions;
using NSubstitute;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.FeatureFlags.Queries;
using Orbit.Application.FeatureFlags.Validators;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.FeatureFlags;

public class FeatureFlagManagementTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    [Theory]
    [InlineData(FeatureFlagActions.EnableForAccounts)]
    [InlineData(FeatureFlagActions.DisableForAccounts)]
    [InlineData(FeatureFlagActions.EnableForEveryone)]
    [InlineData(FeatureFlagActions.DisableForEveryone)]
    [InlineData(FeatureFlagActions.Create)]
    public async Task Validator_AcceptsDocumentedWrites(string action)
    {
        var request = new ManageFeatureFlagsCommand(AdminId, action, "astra_eval_trace", ["account@example.com"], "A description");
        (await new ManageFeatureFlagsCommandValidator().ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "flag", "one line", "a@example.com")]
    [InlineData("create", "", "one line", "a@example.com")]
    [InlineData("create", " flag", "one line", "a@example.com")]
    [InlineData("create", "flag", "", "a@example.com")]
    [InlineData("create", "flag", "first\nsecond", "a@example.com")]
    [InlineData("enable_for_accounts", "flag", "one line", "invalid")]
    public async Task Validator_RejectsInvalidRequests(string action, string key, string description, string email)
    {
        var request = new ManageFeatureFlagsCommand(AdminId, action, key, [email], description);
        (await new ManageFeatureFlagsCommandValidator().ValidateAsync(request)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validators_RejectMissingIdentityAndTargetListAndOversizedValues()
    {
        var validator = new ManageFeatureFlagsCommandValidator();
        (await validator.ValidateAsync(new ManageFeatureFlagsCommand(Guid.Empty, "create", "flag", Description: "Description"))).IsValid.Should().BeFalse();
        (await validator.ValidateAsync(new ManageFeatureFlagsCommand(AdminId, "enable_for_accounts", "flag"))).IsValid.Should().BeFalse();
        (await validator.ValidateAsync(new ManageFeatureFlagsCommand(AdminId, "create", new string('x', 101), Description: "Description"))).IsValid.Should().BeFalse();
        (await validator.ValidateAsync(new ManageFeatureFlagsCommand(AdminId, "create", "flag", Description: new string('x', 501)))).IsValid.Should().BeFalse();
        (await new ListFeatureFlagsQueryValidator().ValidateAsync(new ListFeatureFlagsQuery(Guid.Empty))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Handlers_ForwardIdentityAndActionsToSharedService()
    {
        var service = Substitute.For<IFeatureFlagManagementService>();
        var flag = new AdminFeatureFlag("flag", "Description", false, null, []);
        var write = new ManageFeatureFlagsCommand(AdminId, "create", "flag", Description: "Description");
        service.ManageAsync(write, Arg.Any<CancellationToken>()).Returns(Result.Success(flag));
        service.ListAsync(AdminId, Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<AdminFeatureFlag>>([flag]));

        (await new ManageFeatureFlagsCommandHandler(service).Handle(write, CancellationToken.None)).Value.Should().Be(flag);
        (await new ListFeatureFlagsQueryHandler(service).Handle(new(AdminId), CancellationToken.None)).Value.Should().Equal(flag);
    }
}
