using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Chat.Tools.Implementations;
using MediatR;
using NSubstitute;

namespace Orbit.Application.Tests.Chat;

public class HabitProposalValidatorTests
{
    public const string PortugueseExamMessage = "dia 26/10, exame de colesterol as 15h50, domiciliar dia 30/10, outros exames de sangue as 13h30, domiciliar PRO EXAME DO DIA 30: 3 dias antes, parar com multivitaminico 24 horas antes, nao ir pro muay thay ficar em jejum puro até a coleta";
    public const string EnglishExamMessage = "On 10/26, cholesterol exam at 15:50, at home. On 10/30, other blood tests at 13:30, at home. FOR THE EXAM ON 10/30: 3 days before, stop multivitamins; 24 hours before, don't go to Muay Thai; fast until the collection.";

    [Theory]
    [InlineData(PortugueseExamMessage, "Exame de colesterol", "Outros exames de sangue", "Não tomar multivitamínico", "Não ir ao Muay Thai")]
    [InlineData(EnglishExamMessage, "Cholesterol exam", "Other blood tests", "Don't take multivitamins", "Don't go to Muay Thai")]
    public void OriginalMessageAlone_AcceptsOneTimeEventsAndBoundedCommitments(string message, string first, string second, string vitamins, string training)
    {
        var proposal = ExamProposal(first, second, vitamins, training);

        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
        var parsed = proposal.GetProperty("habits").EnumerateArray()
            .Select(item => BulkCreateHabitsTool.ParseBulkHabitItem(item)!).ToList();
        parsed.Take(2).Should().AllSatisfy(item => item.FrequencyUnit.Should().BeNull());
        parsed[0].DueDate.Should().Be(new DateOnly(2026, 10, 26));
        parsed[0].DueTime.Should().Be(new TimeOnly(15, 50));
        parsed[1].DueDate.Should().Be(new DateOnly(2026, 10, 30));
        parsed[1].DueTime.Should().Be(new TimeOnly(13, 30));
        parsed.Skip(2).Where(item => item.FrequencyUnit is not null).Should().AllSatisfy(item =>
        {
            item.IsBadHabit.Should().BeFalse();
            item.EndDate.Should().Be(new DateOnly(2026, 10, 30));
        });
    }

    public static JsonElement ExamProposal(string first, string second, string vitamins, string training) => JsonSerializer.SerializeToElement(new
    {
        habits = new object[]
        {
            new { title = first, frequency_unit = "OneTime", due_date = "2026-10-26", due_time = "15:50" },
            new { title = second, frequency_unit = "OneTime", due_date = "2026-10-30", due_time = "13:30" },
            new { title = vitamins, frequency_unit = "Day", due_date = "2026-10-27", end_date = "2026-10-30", is_bad_habit = false },
            new { title = training, frequency_unit = "Day", due_date = "2026-10-29", end_date = "2026-10-30", is_bad_habit = false },
            new { title = "Jejum / Fasting", frequency_unit = "OneTime", due_date = "2026-10-30", due_end_time = "13:30" }
        }
    });

    [Theory]
    [InlineData(PortugueseExamMessage, "Não tomar multivitamínico")]
    [InlineData(EnglishExamMessage, "Don't take multivitamins")]
    public void OriginalMessageAlone_RejectsOpenEndedPreparation(string message, string title)
    {
        var proposal = JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", due_date = "2026-10-27" });
        var result = new HabitProposalValidator().Validate(new HabitProposal(message, proposal));
        result.Errors.Should().Contain(error => error.ErrorCode == "TEMPORARY_HABIT_REQUIRES_END");
    }

    [Theory]
    [InlineData(PortugueseExamMessage)]
    [InlineData(EnglishExamMessage)]
    public void BoundedPreparation_RejectsEndAfterEvent(string message)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            title = "Não tomar multivitamínico",
            frequency_unit = "Day",
            due_date = "2026-10-27",
            end_date = "2026-11-30"
        });
        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).Errors
            .Should().Contain(error => error.ErrorCode == "TEMPORARY_HABIT_EVENT_CUTOFF");
    }

    [Theory]
    [InlineData("Exame 26/10. Exame 30/10. Pro exame do dia 26: 3 dias antes, não tomar vitaminas.")]
    [InlineData("Exam 10/26. Exam 10/30. For the exam on 10/26: 3 days before, don't take vitamins.")]
    public void EventReference_UsesTheNamedEventRatherThanTheLastListedDate(string message)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            title = "Não tomar vitaminas",
            frequency_unit = "Day",
            due_date = "2026-10-23",
            end_date = "2026-10-26"
        });
        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Exame dia 30/10 às 13h30", "Exame")]
    [InlineData("Blood test on 10/30 at 13:30", "Blood test")]
    [InlineData("Não ir ao treino amanhã", "Não ir ao treino")]
    [InlineData("Don't train tomorrow", "Don't train")]
    public void SingleDatedInstruction_RejectsRecurringProposal(string message, string title)
    {
        var proposal = JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", due_date = "2026-10-30" });
        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).Errors
            .Should().Contain(error => error.ErrorCode == "DATED_INSTRUCTION_REQUIRES_TASK");
    }

    [Theory]
    [InlineData("Quero estudar todo domingo, e tenho exame no dia 30/10")]
    [InlineData("I study every Sunday, and have an exam on 10/30")]
    public void ExplicitWeekdayRecurrence_RemainsRecurringBesideDatedEvent(string message)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            title = "Study",
            frequency_unit = "Day",
            frequency_quantity = 1,
            days = new[] { "Sunday" }
        });
        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Start running tomorrow, every other day", "Day", 2, false)]
    [InlineData("Começar a correr amanhã, dia sim dia não", "Day", 2, false)]
    [InlineData("Start running tomorrow, three times a week", "Week", 3, true)]
    [InlineData("Começar a correr amanhã, três vezes por semana", "Week", 3, true)]
    public void ExplicitStartDate_DoesNotTurnUnrecognizedCadenceIntoSingleEvent(
        string message, string unit, int quantity, bool flexible)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            title = "Run",
            frequency_unit = unit,
            frequency_quantity = quantity,
            is_flexible = flexible,
            due_date = "2026-10-23"
        });

        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Blood tests tomorrow, every other day", "Blood tests")]
    [InlineData("Exames amanhã, dia sim dia não", "Exames")]
    [InlineData("Don't eat sugar tomorrow, every other day", "Don't eat sugar")]
    [InlineData("Não comer açúcar amanhã, dia sim dia não", "Não comer açúcar")]
    public void RecurringEventOrCommitmentName_DoesNotProveSingleOccurrence(string message, string title)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            title,
            frequency_unit = "Day",
            frequency_quantity = 2,
            due_date = "2026-10-23"
        });

        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("quero parar de fumar", "Fumar")]
    [InlineData("I want to stop smoking", "Smoking")]
    [InlineData("I want to drink less soda", "Drinking soda")]
    public void OngoingReduction_AcceptsAvoidForm(string message, string title)
    {
        var proposal = JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", is_bad_habit = true });
        new HabitProposalValidator().Validate(new HabitProposal(message, proposal)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Não tomar multivitamínico")]
    [InlineData("Sem refrigerante")]
    [InlineData("Parar de fumar")]
    [InlineData("Evitar açúcar")]
    [InlineData("Don't eat sugar")]
    [InlineData("Stop smoking")]
    [InlineData("Avoid soda")]
    public void MixedForm_IsRejectedByProposalValidator(string title)
    {
        var proposal = JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", is_bad_habit = true });
        new HabitProposalValidator().Validate(new HabitProposal(string.Empty, proposal)).Errors
            .Should().Contain(error => error.ErrorCode == "BAD_HABIT_NEGATED_NAME");
    }

    [Theory]
    [InlineData("pt-BR", "27/10/2026")]
    [InlineData("en-US", "10/27/2026")]
    public void TextDates_UseLocaleWhileMachineFieldsStayIso(string locale, string expected)
    {
        var proposal = JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Exam 2026-10-27", description = "Until 2026-10-27", due_date = "2026-10-27" } }
        });
        var localized = HabitProposalValidator.LocalizeTextDates(proposal, locale).GetProperty("habits")[0];
        localized.GetProperty("title").GetString().Should().Be("Exam " + expected);
        localized.GetProperty("description").GetString().Should().Be("Until " + expected);
        localized.GetProperty("due_date").GetString().Should().Be("2026-10-27");
    }

    [Fact]
    public void ToolSchemas_KeepScheduleContractWithoutRepeatingPromptGuidance()
    {
        var tool = new BulkCreateHabitsTool(Substitute.For<IMediator>());
        tool.Description.Should().NotContain(HabitModelInstructions.Schedule).And.NotContain(HabitModelInstructions.Polarity)
            .And.NotContain(HabitModelInstructions.Correction);
        var frequency = JsonSerializer.SerializeToElement(tool.GetParameterSchema()).GetProperty("properties")
            .GetProperty("habits").GetProperty("items").GetProperty("properties").GetProperty("frequency_unit");
        frequency.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).Should().Contain("OneTime");
    }
}
