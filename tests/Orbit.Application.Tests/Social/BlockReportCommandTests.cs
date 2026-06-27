using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class BlockReportCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<Cheer> _cheerRepository = Substitute.For<IGenericRepository<Cheer>>();
    private readonly IGenericRepository<Report> _reportRepository = Substitute.For<IGenericRepository<Report>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly SocialAccessGuard _guard;
    private readonly FriendGraphService _friendGraph;
    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _target = SocialTestHelpers.OptedInUser("Target");

    public BlockReportCommandTests()
    {
        _guard = new SocialAccessGuard(_userRepository);
        _friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        SocialTestHelpers.StubUsers(_userRepository, _caller, _target);
        SocialTestHelpers.StubFind(_blockedUserRepository);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_reportRepository);
        SocialTestHelpers.StubFind(_cheerRepository);
    }

    private BlockUserCommandHandler BlockHandler() =>
        new(_guard, _friendGraph, _userRepository, _blockedUserRepository, _friendshipRepository, _unitOfWork);

    private ReportUserCommandHandler ReportHandler() =>
        new(_guard, _userRepository, _cheerRepository, _reportRepository, _unitOfWork);

    [Fact]
    public async Task Block_CreatesRowAndRemovesFriendship()
    {
        var friendship = Friendship.Create(_caller.Id, _target.Id).Value;
        friendship.Accept();
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var result = await BlockHandler().Handle(new BlockUserCommand(_caller.Id, _target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _blockedUserRepository.Received(1).AddAsync(
            Arg.Is<BlockedUser>(b => b.BlockerId == _caller.Id && b.BlockedId == _target.Id),
            Arg.Any<CancellationToken>());
        _friendshipRepository.Received(1).Remove(friendship);
    }

    [Fact]
    public async Task Block_AlreadyBlocked_IsNoOpSuccess()
    {
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_caller.Id, _target.Id).Value);

        var result = await BlockHandler().Handle(new BlockUserCommand(_caller.Id, _target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _blockedUserRepository.DidNotReceive().AddAsync(Arg.Any<BlockedUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Block_UnknownTarget_ReturnsUserNotFound_WithoutPersisting()
    {
        SocialTestHelpers.StubUsers(_userRepository, _caller);

        var result = await BlockHandler().Handle(new BlockUserCommand(_caller.Id, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
        await _blockedUserRepository.DidNotReceive().AddAsync(Arg.Any<BlockedUser>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unblock_RemovesExistingBlock()
    {
        var block = BlockedUser.Create(_caller.Id, _target.Id).Value;
        SocialTestHelpers.StubFind(_blockedUserRepository, block);

        var handler = new UnblockUserCommandHandler(_guard, _blockedUserRepository, _unitOfWork);
        var result = await handler.Handle(new UnblockUserCommand(_caller.Id, _target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _blockedUserRepository.Received(1).Remove(block);
    }

    [Fact]
    public async Task Unblock_NoBlock_IsNoOpSuccess()
    {
        var handler = new UnblockUserCommandHandler(_guard, _blockedUserRepository, _unitOfWork);
        var result = await handler.Handle(new UnblockUserCommand(_caller.Id, _target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _blockedUserRepository.DidNotReceive().Remove(Arg.Any<BlockedUser>());
    }

    [Fact]
    public async Task Report_CreatesPendingReportWithReasonAndOptionalCheer()
    {
        var cheer = Cheer.Create(_caller.Id, _target.Id, Guid.NewGuid(), "nice").Value;
        SocialTestHelpers.StubFind(_cheerRepository, cheer);

        var result = await ReportHandler().Handle(
            new ReportUserCommand(_caller.Id, _target.Id, ReportReason.Harassment, "abusive", cheer.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _reportRepository.Received(1).AddAsync(
            Arg.Is<Report>(r => r.ReporterId == _caller.Id
                                 && r.ReportedUserId == _target.Id
                                 && r.Reason == ReportReason.Harassment
                                 && r.Details == "abusive"
                                 && r.CheerId == cheer.Id
                                 && r.Status == ReportStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_WithoutCheer_Succeeds()
    {
        var result = await ReportHandler().Handle(
            new ReportUserCommand(_caller.Id, _target.Id, ReportReason.Spam, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _reportRepository.Received(1).AddAsync(
            Arg.Is<Report>(r => r.CheerId == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_CheerNotInvolvingReportedUser_ReturnsCheerNotFound()
    {
        var unrelatedCheer = Cheer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null).Value;
        SocialTestHelpers.StubFind(_cheerRepository, unrelatedCheer);

        var result = await ReportHandler().Handle(
            new ReportUserCommand(_caller.Id, _target.Id, ReportReason.Harassment, null, unrelatedCheer.Id),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.CheerNotFound);
        await _reportRepository.DidNotReceive().AddAsync(Arg.Any<Report>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_CheerDoesNotExist_ReturnsCheerNotFound()
    {
        var result = await ReportHandler().Handle(
            new ReportUserCommand(_caller.Id, _target.Id, ReportReason.Harassment, null, Guid.NewGuid()),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.CheerNotFound);
        await _reportRepository.DidNotReceive().AddAsync(Arg.Any<Report>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Report_UnknownTarget_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubUsers(_userRepository, _caller);

        var result = await ReportHandler().Handle(
            new ReportUserCommand(_caller.Id, Guid.NewGuid(), ReportReason.Spam, null, null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Report_CallerOptedOut_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, optedOut, _target);

        var result = await ReportHandler().Handle(
            new ReportUserCommand(optedOut.Id, _target.Id, ReportReason.Spam, null, null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }
}
