using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Chat;

public class GroundednessCheckerTests
{
    [Fact]
    public void Check_InventedNumber_FlagsClaim()
    {
        GroundednessChecker.Check("You are on a 12-day streak.", TurnFactSet.Empty)
            .Should().Equal(new GroundingViolation("Number", "12"));
    }

    [Theory]
    [InlineData("You completed 1234.5 minutes.", "en")]
    [InlineData("Você completou 1.234,50 minutos.", "pt-BR")]
    [InlineData("You completed 1,234.50 minutes.", "en")]
    [InlineData("Você completou 1234,5 minutos.", "pt-BR")]
    [InlineData("Você completou 1.234,50 minutos.", "en")]
    [InlineData("You completed 1,234.50 minutes.", "pt-BR")]
    public void Check_ReturnedNumber_AcceptsLocalizedFormatting(string text, string language)
    {
        var facts = Facts(new { total = 1234.5m });

        GroundednessChecker.Check(text, facts, language: language).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Você completou 1.234,50 minutos.", "en")]
    [InlineData("You completed 1,234.50 minutes.", "pt-BR")]
    public void Check_LocalizedNumberWithoutFacts_FlagsClaim(string text, string language)
    {
        GroundednessChecker.Check(text, TurnFactSet.Empty, language: language)
            .Should().ContainSingle().Which.Kind.Should().Be("Number");
    }

    [Theory]
    [InlineData("The deadline is April 3, 2026.")]
    [InlineData("O prazo é 3 de abril de 2026.")]
    [InlineData("O prazo é 03/04/2026.")]
    [InlineData("The deadline is 2026-04-03.")]
    public void Check_ReturnedDate_AcceptsProseAndNumericFormats(string text)
    {
        GroundednessChecker.Check(text, Facts(new { deadline = new DateOnly(2026, 4, 3) }))
            .Should().BeEmpty();
    }

    [Fact]
    public void Check_InventedDate_DoesNotAcceptRecombinedDateComponents()
    {
        var facts = Facts(new { deadline = new DateOnly(2026, 4, 3), count = 4 });

        GroundednessChecker.Check("O prazo é 4 de abril de 2026.", facts)
            .Should().ContainSingle().Which.Kind.Should().Be("Date");
    }

    [Fact]
    public void Check_Names_IgnoreCaseAndAccents()
    {
        var facts = Facts(new { title = "Meditação diária" });

        GroundednessChecker.Check("Seu hábito: MEDITACAO DIARIA.", facts, ["Meditação diária"])
            .Should().BeEmpty();
        GroundednessChecker.Check("Seu hábito: meditacao diaria.", TurnFactSet.Empty, ["Meditação diária"])
            .Should().ContainSingle().Which.Kind.Should().Be("EntityName");
    }

    [Fact]
    public void Check_UnknownQuotedEntity_FlagsName()
    {
        GroundednessChecker.Check("Seu hábito \"Natação\" está ativo.", TurnFactSet.Empty)
            .Should().Equal(new GroundingViolation("EntityName", "NATACAO"));
    }

    [Theory]
    [InlineData("Hello! How can I help?")]
    [InlineData("Não tenho esses dados disponíveis.")]
    [InlineData("1. Open the menu.\n2. Choose your habit.")]
    [InlineData("")]
    [InlineData(null)]
    public void Check_NoToolsOrFactualClaims_Passes(string? text)
    {
        GroundednessChecker.Check(text, TurnFactSet.Empty).Should().BeEmpty();
    }

    [Fact]
    public void FromToolResults_FailedToolAndIds_DoNotAuthorizeClaims()
    {
        var facts = TurnFactSet.FromToolResults([
            new AiToolCallResult("query_habits", "call_1", false, null, "12 days", null, new { title = "Meditar" }),
            new AiToolCallResult("query_goals", "call_2", true, null, null, null, new { id = "1234", habit_id = 9876, count = 5 })]);

        facts.Facts.Should().Equal(new GroundingFact("Number", "5", "query_goals", "call_2"));
        GroundednessChecker.Check("You have 1234 habits.", facts).Should().NotBeEmpty();
    }

    [Fact]
    public void FromToolResults_SeparateTurns_DoNotShareFacts()
    {
        var firstTurn = Facts(new { streak = 12 });
        var nextTurn = TurnFactSet.FromToolResults([]);

        GroundednessChecker.Check("Your streak is 12 days.", firstTurn).Should().BeEmpty();
        GroundednessChecker.Check("Your streak is 12 days.", nextTurn).Should().NotBeEmpty();
    }

    [Fact]
    public async Task FromToolResults_RealHabitToolOutput_ProvidesNamesNumbersAndDates()
    {
        var userId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(userId, "Meditação diária", null, null,
            DueDate: new DateOnly(2026, 4, 3))).Value;
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(User.Create("Alex", "alex@example.com").Value);
        var output = await new QueryHabitsTool(habits, users)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), userId, CancellationToken.None);
        var facts = TurnFactSet.FromToolResults([
            new AiToolCallResult("query_habits", "real_output", output.Success,
                output.EntityId, output.EntityName, output.Error, output.Payload)]);

        GroundednessChecker.Check("Você tem 1 hábito: \"meditacao diaria\", com prazo em 3 de abril de 2026.",
            facts, [habit.Title], "pt-BR").Should().BeEmpty();
        facts.Facts.Should().Contain(new GroundingFact("EntityName", "MEDITACAO DIARIA", "query_habits", "real_output"));
    }

    private static TurnFactSet Facts(object payload) => TurnFactSet.FromToolResults([
        new AiToolCallResult("query_habits", "call_1", true, null, null, null, payload)]);
}
