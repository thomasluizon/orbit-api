using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public class RoutineCreationProposalTests
{
    [Theory]
    [InlineData("Create a morning routine: read at 7:00 AM, stretch at 7:30 AM", "Morning routine", "07:00", "07:30")]
    [InlineData("Crie uma rotina da manhã: ler às 7h, alongar às 7h30", "Rotina da manhã", "07:00", "07:30")]
    [InlineData("Every evening: read at 9 PM, lights out at 12 AM", "Evening routine", "21:00", "00:00")]
    [InlineData("Toda noite: ler às 21h, apagar as luzes às 0h", "Rotina da noite", "21:00", "00:00")]
    public void Read_PreservesEachStepTime(string message, string title, string first, string second)
    {
        var proposal = RoutineCreationProposal.Read(message);

        proposal.Should().NotBeNull();
        proposal!.Value.GetProperty("title").GetString().Should().Be(title);
        proposal.Value.TryGetProperty("due_time", out _).Should().BeFalse();
        var children = proposal.Value.GetProperty("sub_habits");
        children[0].GetProperty("due_time").GetString().Should().Be(first);
        children[1].GetProperty("due_time").GetString().Should().Be(second);
    }

    [Theory]
    [InlineData("Create a morning routine on weekdays: read, stretch")]
    [InlineData("Crie uma rotina da manhã nos dias úteis: ler, alongar")]
    public void Read_WeekdaysApplyToParentAndChildren(string message)
    {
        var proposal = RoutineCreationProposal.Read(message);

        proposal.Should().NotBeNull();
        var root = proposal!.Value;
        root.GetProperty("days").EnumerateArray().Select(day => day.GetString()).Should()
            .Equal("Monday", "Tuesday", "Wednesday", "Thursday", "Friday");
        foreach (var child in root.GetProperty("sub_habits").EnumerateArray())
            child.GetProperty("days").GetRawText().Should().Be(root.GetProperty("days").GetRawText());
    }

    [Theory]
    [InlineData("What is my evening routine: read, stretch?")]
    [InlineData("Change my evening routine: read, stretch")]
    [InlineData("Crie uma rotina da noite amanhã: ler, alongar")]
    [InlineData("Create a morning routine: read for three days, stretch")]
    [InlineData("Crie uma rotina da noite: ler por três dias, alongar")]
    [InlineData("Create a morning routine: read on Monday, stretch")]
    [InlineData("Every evening: read, blood test on October 26")]
    [InlineData("Every evening: read, blood test 2026-10-26")]
    [InlineData("Toda noite: ler, exame em 27 de outubro")]
    [InlineData("Every evening: read from 8 to 9, stretch")]
    [InlineData("Every evening: read then stretch, lights out")]
    [InlineData("Create a morning routine: read, stretch and remind me")]
    [InlineData("Create a morning routine: read, stretch, add a tag")]
    [InlineData("Create a morning routine: read at 25:00, stretch")]
    [InlineData("Create a morning routine: read at 7:90, stretch")]
    [InlineData("Create a morning routine: read at 13 PM, stretch")]
    [InlineData("Create a morning routine: read at 7 PM then 8 PM, stretch")]
    [InlineData("Create a morning routine: read, ")]
    [InlineData("Create a morning routine: read")]
    public void Read_LeavesUnresolvedOrCompoundRequestsToNormalInterpretation(string message)
    {
        RoutineCreationProposal.Read(message).Should().BeNull();
    }
}
