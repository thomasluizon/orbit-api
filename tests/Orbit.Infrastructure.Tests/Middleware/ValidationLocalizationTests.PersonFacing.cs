using System.Globalization;
using System.Linq.Expressions;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Validators;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Validators;
using Orbit.Application.Support.Commands;
using Orbit.Application.Support.Validators;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Application.Tags.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Middleware;

public partial class ValidationLocalizationTests
{
    public static IEnumerable<object[]> PersonFacingCopyCases()
    {
        string[][] cases =
        [
            ["SendCode", "Email", "NotEmptyValidator", "", "reader@example.com", "Enter your email", "Digite seu email"],
            ["VerifyCode", "Email", "NotEmptyValidator", "", "reader@example.com", "Enter your email", "Digite seu email"],
            ["SendCode", "Email", "EmailValidator", "reader", "reader@example.com", "Use a complete email, like name@example.com", "Use um email completo, como nome@exemplo.com"],
            ["VerifyCode", "Email", "EmailValidator", "reader", "reader@example.com", "Use a complete email, like name@example.com", "Use um email completo, como nome@exemplo.com"],
            ["VerifyCode", "Code", "NotEmptyValidator", "", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["ApiKeyCode", "Code", "NotEmptyValidator", "", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["DeletionCode", "Code", "NotEmptyValidator", "", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["VerifyCode", "Code", "ExactLengthValidator", "12345", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["ApiKeyCode", "Code", "ExactLengthValidator", "12345", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["DeletionCode", "Code", "ExactLengthValidator", "12345", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["VerifyCode", "Code", "VALIDATION_VERIFICATION_CODE_FORMAT", "1234x6", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["ApiKeyCode", "Code", "VALIDATION_VERIFICATION_CODE_FORMAT", "1234x6", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["DeletionCode", "Code", "VALIDATION_VERIFICATION_CODE_FORMAT", "1234x6", "123456", "Enter all 6 digits", "Digite os 6 dígitos"],
            ["CreateHabit", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["UpdateHabit", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["OnboardingHabit", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["BulkHabit", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["CreateHabit", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["UpdateHabit", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["OnboardingHabit", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["BulkHabit", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["CreateSubHabit", "Title", "VALIDATION_SUB_HABIT_TITLE_REQUIRED", "", "Read", "Give the sub-habit a title", "Dê um título ao sub-hábito"],
            ["UpdateSubHabit", "Title", "VALIDATION_SUB_HABIT_TITLE_REQUIRED", "", "Read", "Give the sub-habit a title", "Dê um título ao sub-hábito"],
            ["CreateSubHabit", "Title", "VALIDATION_SUB_HABIT_TITLE_LENGTH", new string('x', 201), new string('x', 200), "Shorten the sub-habit title to 200 characters or fewer", "Encurte o título do sub-hábito para até 200 caracteres"],
            ["UpdateSubHabit", "Title", "VALIDATION_SUB_HABIT_TITLE_LENGTH", new string('x', 201), new string('x', 200), "Shorten the sub-habit title to 200 characters or fewer", "Encurte o título do sub-hábito para até 200 caracteres"],
            ["CreateHabit", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do hábito para até 10000 caracteres. Você digitou 10001."],
            ["UpdateHabit", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do hábito para até 10000 caracteres. Você digitou 10001."],
            ["OnboardingHabit", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do hábito para até 10000 caracteres. Você digitou 10001."],
            ["CreateSubHabit", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the sub-habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do sub-hábito para até 10000 caracteres. Você digitou 10001."],
            ["UpdateSubHabit", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the sub-habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do sub-hábito para até 10000 caracteres. Você digitou 10001."],
            ["CreateHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do hábito para até 32 caracteres. Você digitou 33."],
            ["UpdateHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do hábito para até 32 caracteres. Você digitou 33."],
            ["OnboardingHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do hábito para até 32 caracteres. Você digitou 33."],
            ["BulkHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do hábito para até 32 caracteres. Você digitou 33."],
            ["CreateSubHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the sub-habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do sub-hábito para até 32 caracteres. Você digitou 33."],
            ["UpdateSubHabit", "Emoji", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the sub-habit emoji to 32 characters or fewer. You entered 33.", "Encurte o emoji do sub-hábito para até 32 caracteres. Você digitou 33."],
            ["CreateHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["UpdateHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["OnboardingHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["BulkHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["CreateSubHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["UpdateSubHabit", "FrequencyQuantity", "GreaterThanValidator", "0", "1", "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"],
            ["CreateTag", "Name", "NotEmptyValidator", "", "Read", "Give the tag a name", "Dê um nome à tag"],
            ["UpdateTag", "Name", "NotEmptyValidator", "", "Read", "Give the tag a name", "Dê um nome à tag"],
            ["CreateTag", "Name", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the tag name to 50 characters or fewer. You entered 51.", "Encurte o nome da tag para até 50 caracteres. Você digitou 51."],
            ["UpdateTag", "Name", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the tag name to 50 characters or fewer. You entered 51.", "Encurte o nome da tag para até 50 caracteres. Você digitou 51."],
            ["CreateTag", "Color", "NotEmptyValidator", "", "#123456", "Choose a color from the swatches", "Escolha uma cor da paleta"],
            ["UpdateTag", "Color", "NotEmptyValidator", "", "#123456", "Choose a color from the swatches", "Escolha uma cor da paleta"],
            ["CreateTag", "Color", "VALIDATION_TAG_COLOR_FORMAT", "red", "#123456", "Choose a color from the swatches", "Escolha uma cor da paleta"],
            ["UpdateTag", "Color", "VALIDATION_TAG_COLOR_FORMAT", "red", "#123456", "Choose a color from the swatches", "Escolha uma cor da paleta"],
            ["CreateGoal", "Title", "NotEmptyValidator", "", "Read", "Give the goal a title", "Dê um título à meta"],
            ["UpdateGoal", "Title", "NotEmptyValidator", "", "Read", "Give the goal a title", "Dê um título à meta"],
            ["OnboardingGoal", "Title", "NotEmptyValidator", "", "Read", "Give the goal a title", "Dê um título à meta"],
            ["CreateGoal", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the goal title to 200 characters or fewer. You entered 201.", "Encurte o título da meta para até 200 caracteres. Você digitou 201."],
            ["UpdateGoal", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the goal title to 200 characters or fewer. You entered 201.", "Encurte o título da meta para até 200 caracteres. Você digitou 201."],
            ["OnboardingGoal", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the goal title to 200 characters or fewer. You entered 201.", "Encurte o título da meta para até 200 caracteres. Você digitou 201."],
            ["CreateGoal", "Description", "MaximumLengthValidator", new string('x', 501), new string('x', 500), "Shorten the goal description to 500 characters or fewer. You entered 501.", "Encurte a descrição da meta para até 500 caracteres. Você digitou 501."],
            ["UpdateGoal", "Description", "MaximumLengthValidator", new string('x', 501), new string('x', 500), "Shorten the goal description to 500 characters or fewer. You entered 501.", "Encurte a descrição da meta para até 500 caracteres. Você digitou 501."],
            ["OnboardingGoal", "Description", "MaximumLengthValidator", new string('x', 501), new string('x', 500), "Shorten the goal description to 500 characters or fewer. You entered 501.", "Encurte a descrição da meta para até 500 caracteres. Você digitou 501."],
            ["CreateGoal", "Unit", "NotEmptyValidator", "", "pages", "Set the unit", "Defina a unidade"],
            ["UpdateGoal", "Unit", "NotEmptyValidator", "", "pages", "Set the unit", "Defina a unidade"],
            ["OnboardingGoal", "Unit", "NotEmptyValidator", "", "pages", "Set the unit", "Defina a unidade"],
            ["CreateGoal", "Unit", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the goal unit to 50 characters or fewer. You entered 51.", "Encurte a unidade da meta para até 50 caracteres. Você digitou 51."],
            ["UpdateGoal", "Unit", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the goal unit to 50 characters or fewer. You entered 51.", "Encurte a unidade da meta para até 50 caracteres. Você digitou 51."],
            ["OnboardingGoal", "Unit", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the goal unit to 50 characters or fewer. You entered 51.", "Encurte a unidade da meta para até 50 caracteres. Você digitou 51."],
            ["CreateGoal", "TargetValue", "GreaterThanValidator", "0", "1", "Set the target to a value greater than 0", "Defina a quantidade da meta como um valor maior que 0"],
            ["UpdateGoal", "TargetValue", "GreaterThanValidator", "0", "1", "Set the target to a value greater than 0", "Defina a quantidade da meta como um valor maior que 0"],
            ["OnboardingGoal", "TargetValue", "GreaterThanValidator", "0", "1", "Set the target to a value greater than 0", "Defina a quantidade da meta como um valor maior que 0"],
            ["GoalProgress", "NewValue", "GreaterThanOrEqualValidator", "-1", "0", "Enter a value of 0 or higher", "Informe um valor igual ou maior que 0"],
            ["GoalProgress", "Note", "MaximumLengthValidator", new string('x', 501), new string('x', 500), "Shorten the progress note to 500 characters or fewer. You entered 501.", "Encurte a nota de progresso para até 500 caracteres. Você digitou 501."],
            ["Support", "Name", "NotEmptyValidator", "", "Reader", "Enter a name.", "Digite um nome."],
            ["Support", "Email", "NotEmptyValidator", "", "reader@example.com", "Enter your email", "Digite seu email"],
            ["Support", "Email", "EmailValidator", "reader", "reader@example.com", "Use a complete email, like name@example.com", "Use um email completo, como nome@exemplo.com"],
            ["Support", "Subject", "NotEmptyValidator", "", "Help", "Pick a subject so we can route it properly.", "Escolha um assunto para a gente encaminhar certo."],
            ["Support", "Subject", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the subject to 200 characters or fewer. You entered 201.", "Encurte o assunto para até 200 caracteres. Você digitou 201."],
            ["Support", "Message", "NotEmptyValidator", "", "Help", "Write at least one sentence about what happened.", "Escreva pelo menos uma frase sobre o que aconteceu."],
            ["Support", "Message", "MaximumLengthValidator", new string('x', 5001), new string('x', 5000), "Shorten your support message to 5000 characters or fewer. You entered 5001.", "Encurte sua mensagem de suporte para até 5000 caracteres. Você digitou 5001."],
            ["Referral", "ReferralCode", "NotEmptyValidator", "", "ABC123", "Enter the referral code", "Digite o código de indicação"],
            ["Referral", "ReferralCode", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the referral code to 50 characters or fewer. You entered 51.", "Encurte o código de indicação para até 50 caracteres. Você digitou 51."],
            ["ProfileName", "Name", "NotEmptyValidator", "", "Reader", "Enter a name.", "Digite um nome."],
            ["ProfileName", "Name", "VALIDATION_PROFILE_NAME_LENGTH", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Keep it under 50 characters.", "Use no máximo 50 caracteres."],
            ["ApiKey", "Name", "VALIDATION_API_KEY_NAME_REQUIRED", "", "Key", "Give the API key a name", "Dê um nome à chave de API"],
            ["ApiKey", "Name", "VALIDATION_API_KEY_NAME_LENGTH", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the API key name to 50 characters or fewer", "Encurte o nome da chave de API para até 50 caracteres"],
            ["Template", "Name", "NotEmptyValidator", "", "List", "Give the checklist template a name", "Dê um nome ao modelo de checklist"],
            ["Template", "Name", "MaximumLengthValidator", new string('x', 101), "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the checklist template name to 100 characters or fewer. You entered 101.", "Encurte o nome do modelo de checklist para até 100 caracteres. Você digitou 101."],
            ["Template", "Items[0]", "VALIDATION_TEMPLATE_ITEM_REQUIRED", "", "Read", "Write the checklist item", "Escreva o item do checklist"],
            ["Template", "Items[0]", "VALIDATION_TEMPLATE_ITEM_LENGTH", new string('x', 501), new string('x', 500), "Shorten the checklist item to 500 characters or fewer", "Encurte o item do checklist para até 500 caracteres"],
            ["Chat", "Message", "VALIDATION_CHAT_MESSAGE_REQUIRED", "", "Read", "Write a message", "Escreva uma mensagem"],
            ["Chat", "Message", "VALIDATION_CHAT_MESSAGE_LENGTH", new string('x', 4001), new string('x', 4000), "Shorten your message to 4000 characters or fewer", "Encurte sua mensagem para até 4000 caracteres"],
            ["CreateHabit", "FrequencyQuantityMissing", "VALIDATION_FREQUENCY_QUANTITY_REQUIRED", "", "1", "Set how often the habit repeats", "Defina com que frequência o hábito se repete"],
            ["UpdateHabit", "FrequencyQuantityMissing", "VALIDATION_FREQUENCY_QUANTITY_REQUIRED", "", "1", "Set how often the habit repeats", "Defina com que frequência o hábito se repete"],
            ["OnboardingHabit", "FrequencyQuantityMissing", "VALIDATION_FREQUENCY_QUANTITY_REQUIRED", "", "1", "Set how often the habit repeats", "Defina com que frequência o hábito se repete"],
            ["BulkHabit", "FrequencyQuantityMissing", "VALIDATION_FREQUENCY_QUANTITY_REQUIRED", "", "1", "Set how often the habit repeats", "Defina com que frequência o hábito se repete"],
            ["SuggestHabitSetup", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["SuggestHabitSetup", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["SuggestTags", "Title", "NotEmptyValidator", "", "Read", "Give the habit a title", "Dê um título ao hábito"],
            ["SuggestTags", "Title", "MaximumLengthValidator", new string('x', 201), new string('x', 200), "Shorten the habit title to 200 characters or fewer. You entered 201.", "Encurte o título do hábito para até 200 caracteres. Você digitou 201."],
            ["SuggestTags", "Description", "MaximumLengthValidator", new string('x', 10001), new string('x', 10000), "Shorten the habit description to 10000 characters or fewer. You entered 10001.", "Encurte a descrição do hábito para até 10000 caracteres. Você digitou 10001."],
            ["BulkHabit", "Tags[0]", "MaximumLengthValidator", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Shorten the tag name to 50 characters or fewer. You entered 51.", "Encurte o nome da tag para até 50 caracteres. Você digitou 51."],
            ["BulkHabit", "TagsCount", "VALIDATION_BULK_TAG_LIMIT", "6", "5", "Select up to 5 tags for this habit", "Escolha até 5 tags para este hábito"],
            ["Template", "ItemsCount", "VALIDATION_TEMPLATE_ITEM_LIMIT", "51", "50", "Add up to 50 checklist items", "Adicione até 50 itens ao checklist"],
        ];
        foreach (var entry in cases)
            foreach (var language in new[] { "en", "pt-BR" })
                yield return entry.Cast<object>().Append(language).ToArray();
    }

    [Theory]
    [MemberData(nameof(PersonFacingCopyCases))]
    public async Task PersonFacingValidators_ReturnLocalizedInstructionsWithUnchangedCodes(
        string flow, string field, string code, string invalid, string valid,
        string english, string portuguese, string language)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            var accepted = await ValidatePersonFacingAsync(flow, field, valid);
            accepted.Errors.Should().NotContain(f => f.PropertyName == ResponseField(flow, field));
            var rejected = await ValidatePersonFacingAsync(flow, field, invalid);
            var failure = rejected.Errors.Should().ContainSingle(f =>
                f.PropertyName == ResponseField(flow, field) && f.ErrorCode == code).Subject;
            failure.ErrorMessage.Should().Be(english);

            var body = await HandleAsync(rejected, language);
            var expected = language == "pt-BR" ? portuguese : english;
            var detail = body.GetProperty("errorDetails").GetProperty(ResponseField(flow, field))
                .EnumerateArray().Single(d => d.GetProperty("code").GetString() == code);
            detail.GetProperty("message").GetString().Should().Be(expected);
            body.GetProperty("errors").GetProperty(ResponseField(flow, field)).EnumerateArray()
                .Select(m => m.GetString()).Should().Contain(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData("CreateHabit", "en")]
    [InlineData("CreateHabit", "pt-BR")]
    [InlineData("CreateSubHabit", "en")]
    [InlineData("CreateSubHabit", "pt-BR")]
    [InlineData("UpdateHabit", "en")]
    [InlineData("UpdateHabit", "pt-BR")]
    [InlineData("OnboardingHabit", "en")]
    [InlineData("OnboardingHabit", "pt-BR")]
    public async Task ChecklistLength_ReturnsLocalizedInstructionWithUnchangedCode(string flow, string language)
    {
        var id = Guid.NewGuid();
        Task<ValidationResult> Check(int length)
        {
            ChecklistItem[] items = [new(new string('x', length), false)];
            return flow switch
            {
                "CreateHabit" => Validate(new CreateHabitCommandValidator(),
                    new CreateHabitCommand(id, "Read", null, null, null, Options: new HabitCommandOptions(ChecklistItems: items))),
                "CreateSubHabit" => Validate(new CreateSubHabitCommandValidator(),
                    new CreateSubHabitCommand(id, id, "Read", null, Options: new HabitCommandOptions(ChecklistItems: items))),
                "UpdateHabit" => Validate(new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>()),
                    new UpdateHabitCommand(id, id, "Read", null, null, null, Options: new UpdateHabitCommandOptions(ChecklistItems: items))),
                "OnboardingHabit" => Validate(new ApplyOnboardingCommandValidator(),
                    new ApplyOnboardingCommand(id, [new ApplyHabitInput("Read", null, null, null, null, ChecklistItems: items)], null, null, null, null)),
                _ => throw new ArgumentOutOfRangeException(nameof(flow))
            };
        }

        (await Check(500)).IsValid.Should().BeTrue();
        var rejected = await Check(501);
        var failure = rejected.Errors.Should().ContainSingle().Subject;
        failure.ErrorCode.Should().Be(ValidationErrorCodes.ChecklistItemLength);
        var body = await HandleAsync(rejected, language);
        var detail = body.GetProperty("errorDetails").GetProperty(failure.PropertyName)[0];
        detail.GetProperty("code").GetString().Should().Be(ValidationErrorCodes.ChecklistItemLength);
        detail.GetProperty("message").GetString().Should().Be(language == "pt-BR"
            ? "Encurte cada item do checklist para até 500 caracteres"
            : "Shorten each checklist item to 500 characters or fewer");
    }

    private static string ResponseField(string flow, string field) => FieldPrefix(flow) +
        (field switch
        {
            "FrequencyQuantityMissing" => "FrequencyQuantity",
            "TagsCount" => "Tags",
            "ItemsCount" => "Items",
            _ => field
        });

    private static string FieldPrefix(string flow) => flow switch
    {
        "OnboardingHabit" or "BulkHabit" => "Habits[0].",
        "OnboardingGoal" => "Goal.",
        _ => ""
    };

    private static Task<ValidationResult> ValidatePersonFacingAsync(string flow, string field, string value)
    {
        var id = Guid.NewGuid();
        string Text(string name, string fallback) => field == name ? value : fallback;
        int? frequency = field.StartsWith("FrequencyQuantity", StringComparison.Ordinal) && value.Length > 0
            ? int.Parse(value, CultureInfo.InvariantCulture) : null;
        FrequencyUnit? unit = field == "FrequencyQuantityMissing" ? FrequencyUnit.Day : null;
        var target = field == "TargetValue" ? decimal.Parse(value, CultureInfo.InvariantCulture) : 1;
        return flow switch
        {
            "SuggestHabitSetup" => Validate(new SuggestHabitSetupCommandValidator(), new SuggestHabitSetupCommand(id, value, "en")),
            "SuggestTags" => Validate(new SuggestTagsQueryValidator(), new SuggestTagsQuery(id, Text("Title", "Read"), Text("Description", "Notes"), "en")),
            "SendCode" => Validate(new SendCodeCommandValidator(), new SendCodeCommand(Text("Email", "reader@example.com"))),
            "VerifyCode" => Validate(new VerifyCodeCommandValidator(), new VerifyCodeCommand(Text("Email", "reader@example.com"), Text("Code", "123456"))),
            "ApiKeyCode" => Validate(new ConfirmApiKeyCreationChallengeCommandValidator(), new ConfirmApiKeyCreationChallengeCommand(id, value)),
            "DeletionCode" => Validate(new ConfirmAccountDeletionCommandValidator(), new ConfirmAccountDeletionCommand(id, value)),
            "CreateHabit" => Validate(new CreateHabitCommandValidator(), new CreateHabitCommand(id, Text("Title", "Read"), Text("Description", "Notes"), unit, frequency, Emoji: Text("Emoji", "📚"))),
            "CreateSubHabit" => Validate(new CreateSubHabitCommandValidator(), new CreateSubHabitCommand(id, id, Text("Title", "Read"), Text("Description", "Notes"), FrequencyQuantity: frequency, Emoji: Text("Emoji", "📚"))),
            "UpdateHabit" or "UpdateSubHabit" => ValidateUpdatedHabitAsync(flow == "UpdateSubHabit", field, value),
            "OnboardingHabit" => Validate(new ApplyOnboardingCommandValidator(), new ApplyOnboardingCommand(id, [new ApplyHabitInput(Text("Title", "Read"), Text("Description", "Notes"), Text("Emoji", "📚"), unit, frequency)], null, null, null, null)),
            "BulkHabit" => Validate(new BulkCreateHabitsCommandValidator(), new BulkCreateHabitsCommand(id, [new BulkHabitItem(Text("Title", "Read"), null, unit, frequency, Emoji: Text("Emoji", "📚"), Tags: field == "Tags[0]" ? [value]
                : field == "TagsCount" ? Enumerable.Repeat("Read", int.Parse(value, CultureInfo.InvariantCulture)).ToArray() : null)])),
            "CreateTag" => Validate(new CreateTagCommandValidator(), new CreateTagCommand(id, Text("Name", "Read"), Text("Color", "#123456"))),
            "UpdateTag" => Validate(new UpdateTagCommandValidator(), new UpdateTagCommand(id, id, Text("Name", "Read"), Text("Color", "#123456"))),
            "CreateGoal" => Validate(new CreateGoalCommandValidator(), new CreateGoalCommand(id, Text("Title", "Read"), Text("Description", "Notes"), target, Text("Unit", "pages"), null)),
            "UpdateGoal" => Validate(new UpdateGoalCommandValidator(), new UpdateGoalCommand(id, id, Text("Title", "Read"), Text("Description", "Notes"), target, Text("Unit", "pages"), null)),
            "OnboardingGoal" => Validate(new ApplyOnboardingCommandValidator(), new ApplyOnboardingCommand(id, [], null, new ApplyGoalInput(Text("Title", "Read"), Text("Description", "Notes"), target, Text("Unit", "pages")), null, null)),
            "GoalProgress" => Validate(new UpdateGoalProgressCommandValidator(), new UpdateGoalProgressCommand(id, id, field == "NewValue" ? decimal.Parse(value, CultureInfo.InvariantCulture) : 0, Text("Note", "Notes"))),
            "Support" => Validate(new SendSupportCommandValidator(), new SendSupportCommand(id, Text("Name", "Reader"), Text("Email", "reader@example.com"), Text("Subject", "Help"), Text("Message", "Help"))),
            "Referral" => Validate(new ProcessReferralCodeCommandValidator(), new ProcessReferralCodeCommand(id, value)),
            "ProfileName" => Validate(new SetNameCommandValidator(), new SetNameCommand(id, value)),
            "ApiKey" => Validate(new CreateApiKeyValidator(), new CreateApiKeyCommand(id, value)),
            "Template" => Validate(new CreateChecklistTemplateCommandValidator(), new CreateChecklistTemplateCommand(id, Text("Name", "List"), field == "ItemsCount"
                ? Enumerable.Repeat("Read", int.Parse(value, CultureInfo.InvariantCulture)).ToArray() : [Text("Items[0]", "Read")])),
            "Chat" => Validate(new ProcessUserChatCommandValidator(), new ProcessUserChatCommand(id, value)),
            _ => throw new ArgumentOutOfRangeException(nameof(flow))
        };
    }

    private static Task<ValidationResult> Validate<T>(IValidator<T> validator, T command) =>
        validator.ValidateAsync(command);

    private static Task<ValidationResult> ValidateUpdatedHabitAsync(bool isChild, string field, string value)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", null, null, new DateOnly(2030, 1, 1),
            ParentHabitId: isChild ? Guid.NewGuid() : null)).Value;
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(habit);
        var command = new UpdateHabitCommand(habit.UserId, habit.Id,
            field == "Title" ? value : "Read", field == "Description" ? value : null, field == "FrequencyQuantityMissing" ? FrequencyUnit.Day : null,
            field.StartsWith("FrequencyQuantity", StringComparison.Ordinal) && value.Length > 0
                ? int.Parse(value, CultureInfo.InvariantCulture) : null,
            Emoji: field == "Emoji" ? value : null);
        return Validate(new UpdateHabitCommandValidator(repository), command);
    }
}
