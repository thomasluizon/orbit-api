using System.Security.Claims;
using System.Text.Json;
using Xunit.Abstractions;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Application.Uploads.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Validators;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Validators;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Validators;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Chat.Models;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Email.Commands;
using Orbit.Application.Email.Validators;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Validators;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Validators;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Validators;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Validators;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Validators;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Validators;
using Orbit.Application.Support.Commands;
using Orbit.Application.Support.Validators;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;
using Orbit.Application.Uploads.Commands;
using Orbit.Application.Uploads.Validators;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Validators;
using Orbit.Application.Waitlist.Commands;
using Orbit.Application.Waitlist.Validators;

namespace Orbit.Infrastructure.Tests.Middleware;

public class ValidationLocalizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Accountability")]
    [InlineData("ApiKeys")]
    [InlineData("Auth")]
    [InlineData("Calendar")]
    [InlineData("Challenges")]
    [InlineData("Chat")]
    [InlineData("ChecklistTemplates")]
    [InlineData("Email")]
    [InlineData("Gamification")]
    [InlineData("Goals")]
    [InlineData("Habits")]
    [InlineData("Marketing")]
    [InlineData("Notifications")]
    [InlineData("Profile")]
    [InlineData("Referrals")]
    [InlineData("Social")]
    [InlineData("Subscriptions")]
    [InlineData("Support")]
    [InlineData("Tags")]
    [InlineData("Uploads")]
    [InlineData("UserFacts")]
    [InlineData("Waitlist")]
    public async Task EveryValidatorFamily_ReturnsPortugueseCopyAndAlignedCodes(string family)
    {
        var (result, property, code, portuguese) = FailureFor(family);
        output.WriteLine(JsonSerializer.Serialize(result.Errors));
        result.Errors.Should().Contain(f => f.PropertyName == property && f.ErrorCode == code);

        var englishBody = await HandleAsync(result, "en");
        var portugueseBody = await HandleAsync(result, "pt-BR");
        var translated = portugueseBody.GetProperty("errorDetails").GetProperty(property)
            .EnumerateArray().Single(e => e.GetProperty("code").GetString() == code);
        translated.GetProperty("message").GetString().Should().Be(portuguese);

        foreach (var group in result.Errors.GroupBy(f => f.PropertyName))
        {
            var english = englishBody.GetProperty("errors").GetProperty(group.Key);
            var localized = portugueseBody.GetProperty("errors").GetProperty(group.Key);
            var details = portugueseBody.GetProperty("errorDetails").GetProperty(group.Key);
            var englishDetails = englishBody.GetProperty("errorDetails").GetProperty(group.Key);
            english.EnumerateArray().Select(e => e.GetString()).Should().Equal(group.Select(f => f.LocalizedMessage(false)));
            localized.GetArrayLength().Should().Be(group.Count());
            details.EnumerateArray().Select(e => e.GetProperty("code").GetString())
                .Should().Equal(group.Select(f => f.ErrorCode));
            englishDetails.EnumerateArray().Select(e => e.GetProperty("code").GetString())
                .Should().Equal(group.Select(f => f.ErrorCode));
            details.EnumerateArray().Select(e => e.GetProperty("message").GetString())
                .Should().Equal(localized.EnumerateArray().Select(e => e.GetString()));
            for (var i = 0; i < group.Count(); i++)
            {
                localized[i].GetString().Should().NotBe(english[i].GetString());
                localized[i].GetString().Should().NotMatchRegex(@"\{[A-Za-z0-9]+\}");
            }
        }
    }

    [Theory]
    [InlineData(null, "pt-BR", "Digite os 6 dígitos")]
    [InlineData("en", "pt-BR", "Digite os 6 dígitos")]
    [InlineData("pt-BR", "en", "Enter all 6 digits")]
    [InlineData("pt-BR", null, "Digite os 6 dígitos")]
    [InlineData(null, null, "Enter all 6 digits")]
    public async Task StoredLanguageAndHeader_UseTheDomainResolverPrecedence(
        string? header, string? storedLanguage, string expected)
    {
        var (result, _, _, _) = FailureFor("Auth");
        var body = await HandleAsync(result, header, storedLanguage, authenticated: true);

        body.GetProperty("errors").GetProperty("Code")[1].GetString().Should().Be(expected);
    }

    [Fact]
    public async Task FluentValidationPlaceholders_SurviveLocalization()
    {
        var id = Guid.NewGuid();
        var key = new CreateApiKeyValidator().Validate(new CreateApiKeyCommand(id, "Key", ["unknown:scope"]));
        var keyBody = await HandleAsync(key, "pt-BR");
        keyBody.GetProperty("errors").GetProperty("Scopes[0]")[0].GetString()
            .Should().Be("A permissão 'unknown:scope' da chave de API não é reconhecida.");

        var code = new VerifyCodeCommandValidator().Validate(new VerifyCodeCommand("reader@example.com", "12345"));
        var codeBody = await HandleAsync(code, "pt-BR");
        codeBody.GetProperty("errors").GetProperty("Code")[0].GetString()
            .Should().Be("Digite 6 caracteres em 'Code'. Você digitou 5 caracteres.");
    }

    [Fact]
    public async Task CountedCopy_UsesTheArgumentsFromTheRealValidator()
    {
        var request = new ResolveClarificationRequest(new string('x', AppConstants.MaxClarificationValueLength + 1));
        var result = new ResolveClarificationRequestValidator().Validate(request);
        var body = await HandleAsync(result, "pt-BR");

        body.GetProperty("errors").GetProperty("Value")[0].GetString()
            .Should().Contain(AppConstants.MaxClarificationValueLength.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain("{0}");
        body.GetProperty("errorDetails").GetProperty("Value")[0].GetProperty("code").GetString()
            .Should().Be(ErrorMessages.ClarificationValueTooLong.Code);
    }

    [Theory]
    [InlineData("Login", "en", "Enter all 6 digits")]
    [InlineData("Login", "pt-BR", "Digite os 6 dígitos")]
    [InlineData("ApiKey", "en", "Enter all 6 digits")]
    [InlineData("ApiKey", "pt-BR", "Digite os 6 dígitos")]
    [InlineData("Deletion", "en", "Enter all 6 digits")]
    [InlineData("Deletion", "pt-BR", "Digite os 6 dígitos")]
    public async Task FiveDigitCode_ReturnsTheInstructionInEveryPersonFacingFlow(
        string flow, string language, string expected)
    {
        var id = Guid.NewGuid();
        ValidationResult Validate(string code) => flow switch
        {
            "Login" => new VerifyCodeCommandValidator().Validate(new VerifyCodeCommand("reader@example.com", code)),
            "ApiKey" => new ConfirmApiKeyCreationChallengeCommandValidator().Validate(new ConfirmApiKeyCreationChallengeCommand(id, code)),
            "Deletion" => new ConfirmAccountDeletionCommandValidator().Validate(new ConfirmAccountDeletionCommand(id, code)),
            _ => throw new ArgumentOutOfRangeException(nameof(flow))
        };

        Validate("123456").IsValid.Should().BeTrue();
        var result = Validate("12345");
        var body = await HandleAsync(result, language);
        var failure = body.GetProperty("errorDetails").GetProperty("Code").EnumerateArray()
            .Single(detail => detail.GetProperty("code").GetString() == ValidationErrorCodes.VerificationCodeFormat);

        failure.GetProperty("message").GetString().Should().Be(expected);
        body.GetProperty("errors").GetProperty("Code").EnumerateArray()
            .Select(message => message.GetString()).Should().Contain(expected);
    }

    private static (ValidationResult Result, string Property, string Code, string Portuguese) Case<T>(
        IValidator<T> validator, T request, string property, string code, string portuguese) =>
        (validator.Validate(request), property, code, portuguese);

    private static (ValidationResult Result, string Property, string Code, string Portuguese) FailureFor(string family)
    {
        var id = Guid.NewGuid();
        return family switch
        {
            "Accountability" => Case(new SetAccountabilityHabitsCommandValidator(), new SetAccountabilityHabitsCommand(id, id, [Guid.Empty]), "HabitIds", ValidationErrorCodes.HabitIdsRequired, "Os IDs dos hábitos não podem estar vazios."),
            "ApiKeys" => Case(new CreateApiKeyValidator(), new CreateApiKeyCommand(id, ""), "Name", ValidationErrorCodes.ApiKeyNameRequired, "O nome da chave de API é obrigatório."),
            "Auth" => Case(new VerifyCodeCommandValidator(), new VerifyCodeCommand("reader@example.com", "12345"), "Code", ValidationErrorCodes.VerificationCodeFormat, "Digite os 6 dígitos"),
            "Calendar" => Case(new SetSelectedCalendarsCommandValidator(), new SetSelectedCalendarsCommand(id, [""]), "CalendarIds[0]", ValidationErrorCodes.CalendarIdsRequired, "Os IDs das agendas não podem estar vazios."),
            "Challenges" => Case(new SetChallengeHabitsCommandValidator(), new SetChallengeHabitsCommand(id, id, []), "HabitIds", ValidationErrorCodes.ChallengeHabitsRequired, "Vincule pelo menos um dos seus hábitos ao desafio."),
            "Chat" => Case(new TranscribeAudioCommandValidator(), new TranscribeAudioCommand(id, [1], "voice.xyz"), "FileName", ValidationErrorCodes.AudioFormat, "O formato de áudio '.xyz' não é suportado."),
            "ChecklistTemplates" => Case(new CreateChecklistTemplateCommandValidator(), new CreateChecklistTemplateCommand(id, "List", [""]), "Items[0]", ValidationErrorCodes.TemplateItemRequired, "Os itens do modelo de checklist não podem estar vazios."),
            "Email" => Case(new ProcessSesEventCommandValidator(), new ProcessSesEventCommand(""), "Payload", "NotEmptyValidator", "Preencha 'Payload'."),
            "Gamification" => Case(new ReportEventCommandValidator(), new ReportEventCommand(id, "unknown"), "EventKey", ValidationErrorCodes.EventKeyKnown, "A chave do evento é desconhecida."),
            "Goals" => Case(new LinkHabitsToGoalCommandValidator(), new LinkHabitsToGoalCommand(id, id, Enumerable.Repeat(id, AppConstants.MaxHabitsPerGoal + 1).ToArray()), "HabitIds", ValidationErrorCodes.GoalHabitLimit, $"Vincule até {AppConstants.MaxHabitsPerGoal} hábitos a esta meta."),
            "Habits" => Case(new CreateHabitCommandValidator(), new CreateHabitCommand(id, "Read", null, null, null, IsBadHabit: true, IsGeneral: true), "IsBadHabit", ValidationErrorCodes.GeneralHabitNotBad, "Hábitos gerais não podem ser hábitos ruins"),
            "Marketing" => Case(new SendMarketingBroadcastCommandValidator(), new SendMarketingBroadcastCommand("", "Subject", "Body", "Body", null), "SubjectEn", "NotEmptyValidator", "Preencha 'Subject En'."),
            "Notifications" => Case(new UnsubscribePushCommandValidator(), new UnsubscribePushCommand(id, ""), "Endpoint", "NotEmptyValidator", "Preencha 'Endpoint'."),
            "Profile" => Case(new SetLanguageCommandValidator(), new SetLanguageCommand(id, "fr"), "Language", ValidationErrorCodes.LanguageSupported, "O idioma deve ser um destes: en, pt-BR"),
            "Referrals" => Case(new ProcessReferralCodeCommandValidator(), new ProcessReferralCodeCommand(id, ""), "ReferralCode", "NotEmptyValidator", "Preencha 'Referral Code'."),
            "Social" => Case(new GetCheersQueryValidator(), new GetCheersQuery(id, "unknown"), "Direction", ValidationErrorCodes.CheersDirection, "A direção deve ser 'received' ou 'sent'."),
            "Subscriptions" => Case(new CreateCheckoutCommandValidator(), new CreateCheckoutCommand(id, "weekly", null, null), "Interval", ValidationErrorCodes.BillingIntervalSupported, "O intervalo de cobrança deve ser 'monthly' ou 'yearly'."),
            "Support" => Case(new SendSupportCommandValidator(), new SendSupportCommand(id, "Reader", "reader@example.com", "", "Help"), "Subject", "NotEmptyValidator", "Preencha 'Subject'."),
            "Tags" => Case(new CreateTagCommandValidator(), new CreateTagCommand(id, "Read", "invalid"), "Color", ValidationErrorCodes.TagColorFormat, "Escolha uma cor de tag válida"),
            "Uploads" => Case(new SignUploadValidator(), new SignUploadCommand(id, "invalid", 1), "ContentType", ValidationErrorCodes.UploadContentTypeSupported, $"O tipo de conteúdo deve ser um destes: {string.Join(", ", UploadContentTypes.Allowed)}."),
            "UserFacts" => Case(new BulkDeleteUserFactsCommandValidator(), new BulkDeleteUserFactsCommand(id, []), "FactIds", ValidationErrorCodes.FactIdsRequired, "A lista de IDs dos fatos não pode estar vazia"),
            "Waitlist" => Case(new JoinWaitlistCommandValidator(), new JoinWaitlistCommand("reader@example.com", "fr"), "Language", ValidationErrorCodes.WaitlistLanguageSupported, "O idioma deve ser um destes: en, pt-BR."),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private static async Task<JsonElement> HandleAsync(
        ValidationResult result, string? header, string? storedLanguage = null, bool authenticated = false)
    {
        var repository = Substitute.For<IGenericRepository<User>>();
        var context = new DefaultHttpContext { TraceIdentifier = "validation-localization" };
        context.Response.Body = new MemoryStream();
        if (header is not null)
            context.Request.Headers.AcceptLanguage = header;
        if (authenticated)
        {
            var user = User.Create("Reader", "reader@example.com").Value;
            user.SetLanguage(storedLanguage);
            repository.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
        }

        using var services = new ServiceCollection()
            .AddSingleton<IRequestLanguageResolver>(new RequestLanguageResolver(repository))
            .BuildServiceProvider();
        context.RequestServices = services;

        await new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance)
            .TryHandleAsync(context, new ValidationException(result.Errors), CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
