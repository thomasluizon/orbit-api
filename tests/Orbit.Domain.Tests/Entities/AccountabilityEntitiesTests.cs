using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class AccountabilityEntitiesTests
{
    [Fact]
    public void Pair_Create_DifferentUsers_ReturnsPending()
    {
        var requesterId = Guid.NewGuid();
        var addresseeId = Guid.NewGuid();

        var result = AccountabilityPair.Create(requesterId, addresseeId, AccountabilityCadence.Weekly);

        result.IsSuccess.Should().BeTrue();
        result.Value.RequesterId.Should().Be(requesterId);
        result.Value.AddresseeId.Should().Be(addresseeId);
        result.Value.Status.Should().Be(AccountabilityPairStatus.Pending);
        result.Value.Cadence.Should().Be(AccountabilityCadence.Weekly);
        result.Value.AcceptedAtUtc.Should().BeNull();
        result.Value.EndedAtUtc.Should().BeNull();
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pair_Create_SameUser_ReturnsCannotPairSelf()
    {
        var userId = Guid.NewGuid();

        var result = AccountabilityPair.Create(userId, userId, AccountabilityCadence.Daily);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotPairSelf.Code);
    }

    [Fact]
    public void Pair_Accept_PendingPair_TransitionsToAccepted()
    {
        var pair = AccountabilityPair.Create(Guid.NewGuid(), Guid.NewGuid(), AccountabilityCadence.Daily).Value;

        var result = pair.Accept();

        result.IsSuccess.Should().BeTrue();
        pair.Status.Should().Be(AccountabilityPairStatus.Accepted);
        pair.AcceptedAtUtc.Should().NotBeNull();
        pair.AcceptedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pair_Accept_AlreadyAccepted_ReturnsPairNotPending()
    {
        var pair = AccountabilityPair.Create(Guid.NewGuid(), Guid.NewGuid(), AccountabilityCadence.Daily).Value;
        pair.Accept();

        var result = pair.Accept();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.PairNotPending.Code);
        pair.Status.Should().Be(AccountabilityPairStatus.Accepted);
    }

    [Fact]
    public void Pair_End_PendingPair_TransitionsToEnded()
    {
        var pair = AccountabilityPair.Create(Guid.NewGuid(), Guid.NewGuid(), AccountabilityCadence.Daily).Value;

        pair.End();

        pair.Status.Should().Be(AccountabilityPairStatus.Ended);
        pair.EndedAtUtc.Should().NotBeNull();
        pair.EndedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Pair_End_AcceptedPair_TransitionsToEnded()
    {
        var pair = AccountabilityPair.Create(Guid.NewGuid(), Guid.NewGuid(), AccountabilityCadence.Daily).Value;
        pair.Accept();

        pair.End();

        pair.Status.Should().Be(AccountabilityPairStatus.Ended);
        pair.EndedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void CheckIn_Create_ValidInput_ReturnsSuccess()
    {
        var pairId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var date = new DateOnly(2026, 6, 30);

        var result = AccountabilityCheckIn.Create(pairId, userId, date, "Done today");

        result.IsSuccess.Should().BeTrue();
        result.Value.PairId.Should().Be(pairId);
        result.Value.UserId.Should().Be(userId);
        result.Value.Date.Should().Be(date);
        result.Value.Note.Should().Be("Done today");
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CheckIn_Create_NoteExceedsMaxLength_ReturnsAccountabilityNoteTooLong()
    {
        var note = new string('a', DomainConstants.MaxAccountabilityNoteLength + 1);

        var result = AccountabilityCheckIn.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30), note);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.AccountabilityNoteTooLong.Code);
    }

    [Fact]
    public void CheckIn_Create_NoteAtMaxLength_ReturnsSuccess()
    {
        var note = new string('a', DomainConstants.MaxAccountabilityNoteLength);

        var result = AccountabilityCheckIn.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30), note);

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().Be(note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckIn_Create_BlankNote_StoresNull(string? note)
    {
        var result = AccountabilityCheckIn.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30), note);

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().BeNull();
    }

    [Fact]
    public void CheckIn_Create_NoteWithSurroundingWhitespace_IsTrimmed()
    {
        var result = AccountabilityCheckIn.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 6, 30), "  Keep going  ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().Be("Keep going");
    }

    [Fact]
    public void PairHabit_Create_SetsAllFields()
    {
        var pairId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var habitId = Guid.NewGuid();

        var pairHabit = AccountabilityPairHabit.Create(pairId, userId, habitId);

        pairHabit.PairId.Should().Be(pairId);
        pairHabit.UserId.Should().Be(userId);
        pairHabit.HabitId.Should().Be(habitId);
        pairHabit.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
