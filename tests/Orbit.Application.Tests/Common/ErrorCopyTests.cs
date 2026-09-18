using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Common;

/// <summary>
/// The catalog is the only thing standing between a domain guard's developer-facing sentence
/// and a person's screen, so these tests assert it is total rather than sampling it. A new
/// error constant that arrives without copy fails here, which is what keeps the raw-message
/// fallback in the response path unreachable.
/// </summary>
public class ErrorCopyTests
{
    private const string DoubledHyphen = "--";
    /// <summary>
    /// The two banned dashes are built from their code points instead of typed, because a file
    /// that detects a dash by embedding one still embeds one, and the Dash Ban gate scans this
    /// file like any other. U+2014 is the em dash and U+2013 is the en dash.
    /// </summary>
    private static readonly string EmDash = ((char)0x2014).ToString();

    private static readonly string EnDash = ((char)0x2013).ToString();

    private static readonly Regex BareCodePattern = new("[A-Z]{2,}_[A-Z_]+", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex PlaceholderPattern = new(@"\{\d+\}", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static IEnumerable<string> DeclaredErrorCodes() =>
        typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static IEnumerable<AppError> DeclaredErrors(Type catalogType) =>
        catalogType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(AppError))
            .Select(field => (AppError)field.GetValue(null)!);

    public static TheoryData<string, string, string> EveryCopyEntry()
    {
        var entries = new TheoryData<string, string, string>();
        foreach (var (code, copy) in ErrorCopy.All)
            entries.Add(code, copy.En, copy.PtBr);
        return entries;
    }

    [Fact]
    public void EveryDeclaredErrorCodeHasCopy()
    {
        var missing = DeclaredErrorCodes().Where(code => !ErrorCopy.All.ContainsKey(code)).ToList();

        missing.Should().BeEmpty("every code in ErrorCodes must carry user-facing copy");
    }

    [Fact]
    public void EveryApplicationErrorConstantHasCopy()
    {
        var missing = DeclaredErrors(typeof(ErrorMessages))
            .Select(error => error.Code)
            .Where(code => !ErrorCopy.All.ContainsKey(code))
            .Distinct()
            .ToList();

        missing.Should().BeEmpty("every ErrorMessages constant must carry user-facing copy");
    }

    [Fact]
    public void EveryDomainErrorConstantHasCopy()
    {
        var missing = DeclaredErrors(typeof(DomainErrors))
            .Select(error => error.Code)
            .Where(code => !ErrorCopy.All.ContainsKey(code))
            .Distinct()
            .ToList();

        missing.Should().BeEmpty(
            "a domain guard fires on ordinary input, so every DomainErrors constant needs copy a person can read");
    }

    [Fact]
    public void NoDomainGuardSentenceIsItsOwnUserFacingCopy()
    {
        var leaked = DeclaredErrors(typeof(DomainErrors))
            .Where(error => ErrorCopy.All.TryGetValue(error.Code, out var copy)
                            && (copy.En == error.Message || copy.PtBr == error.Message))
            .Select(error => error.Code)
            .ToList();

        leaked.Should().BeEmpty("the developer-facing sentence must never be what a person reads");
    }

    [Fact]
    public void TheDaysRequireQuantityOneLeakResolvesToWrittenCopyInBothLanguages()
    {
        const string developerSentence = "Days can only be set when frequency quantity is 1.";
        DomainErrors.DaysRequireQuantityOne.Message.Should().Be(developerSentence);

        ErrorCopy.TryResolve(DomainErrors.DaysRequireQuantityOne.Code, isPtBr: false, [], out var english)
            .Should().BeTrue();
        ErrorCopy.TryResolve(DomainErrors.DaysRequireQuantityOne.Code, isPtBr: true, [], out var portuguese)
            .Should().BeTrue();

        english.Should().NotBe(developerSentence);
        portuguese.Should().NotBe(developerSentence);
        english.Should().Contain("once a day");
        portuguese.Should().Contain("uma vez ao dia");
    }

    [Theory]
    [MemberData(nameof(EveryCopyEntry))]
    public void EveryEntryCarriesBothLanguages(string code, string english, string portuguese)
    {
        english.Should().NotBeNullOrWhiteSpace($"{code} needs English copy");
        portuguese.Should().NotBeNullOrWhiteSpace($"{code} needs pt-BR copy");
        portuguese.Should().NotBe(english, $"{code} must be translated, not copied");
    }

    [Theory]
    [MemberData(nameof(EveryCopyEntry))]
    public void EveryEntryObeysTheErrorVoice(string code, string english, string portuguese)
    {
        foreach (var copy in new[] { english, portuguese })
        {
            copy.Should().NotContain("!", $"{code} must stay calm");
            copy.Should().NotContain(EmDash, $"{code} must carry no em dash");
            copy.Should().NotContain(EnDash, $"{code} must carry no en dash");
            copy.Should().NotContain(DoubledHyphen, $"{code} must carry no doubled hyphen");
            copy.Should().NotContainEquivalentOf("something went wrong", $"{code} must name what happened");
            copy.Should().NotContainEquivalentOf("oops", $"{code} must not joke about a failure");
            BareCodePattern.IsMatch(copy).Should().BeFalse($"{code} must not show a bare error code");
        }
    }

    [Theory]
    [MemberData(nameof(EveryCopyEntry))]
    public void BothLanguagesTakeTheSamePlaceholders(string code, string english, string portuguese)
    {
        var inEnglish = PlaceholderPattern.Matches(english).Select(match => match.Value).OrderBy(value => value);
        var inPortuguese = PlaceholderPattern.Matches(portuguese).Select(match => match.Value).OrderBy(value => value);

        inPortuguese.Should().Equal(inEnglish, $"{code} would format differently in one language");
    }

    [Fact]
    public void EveryApplicationConstantTakesItsMessageFromTheCatalog()
    {
        foreach (var error in DeclaredErrors(typeof(ErrorMessages)))
        {
            var allowed = ErrorCopy.AllCounted.TryGetValue(error.Code, out var counted)
                ? new[] { ErrorCopy.All[error.Code].En, counted.En }
                : [ErrorCopy.All[error.Code].En];

            allowed.Should().Contain(error.Message, $"{error.Code} must not be written twice");
        }
    }

    public static TheoryData<string, string, string> EveryCountedEntry()
    {
        var entries = new TheoryData<string, string, string>();
        foreach (var (code, copy) in ErrorCopy.AllCounted)
            entries.Add(code, copy.En, copy.PtBr);
        return entries;
    }

    /// <summary>
    /// A counted variant answers a code the plain catalog already answers, so it stays inside the
    /// same totality guarantee: the fallback in the response path must stay unreachable whether or
    /// not the failure carried a count.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCountedEntry))]
    public void EveryCountedVariantAnswersAKnownCodeAndTakesACount(string code, string english, string portuguese)
    {
        ErrorCopy.All.Should().ContainKey(code);

        foreach (var copy in new[] { english, portuguese })
        {
            copy.Should().NotContain("!", $"{code} must stay calm");
            copy.Should().NotContain(EmDash, $"{code} must carry no em dash");
            copy.Should().NotContain(EnDash, $"{code} must carry no en dash");
            copy.Should().NotContain(DoubledHyphen, $"{code} must carry no doubled hyphen");
            PlaceholderPattern.IsMatch(copy).Should().BeTrue($"{code} is only selected when a count is present");
        }
    }

    /// <summary>
    /// Orbit 1.3.31 and the web step-up screen read the remaining-attempt count out of this
    /// message with <c>/remaining attempts:\s*(\d+)\s*$/i</c> and render it from their own
    /// localized plural strings. The token is therefore a wire contract in both languages, and a
    /// translated one would drop the count for every pt-BR reader.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheAttemptsVariantKeepsTheTokenTheShippedClientsParse(bool isPtBr)
    {
        var formatted = ErrorMessages.InvalidDeletionCode.Format(2);

        formatted.Code.Should().Be(ErrorCodes.InvalidVerificationCode);
        ErrorCopy.TryResolve(formatted.Code, isPtBr, formatted.Args, out var message).Should().BeTrue();
        message.Should().MatchRegex(@"[Rr]emaining attempts:\s*2\s*$");
    }

    [Fact]
    public void AFailureWithoutACountStillGetsThePlainSentence()
    {
        ErrorCopy.TryResolve(ErrorCodes.InvalidVerificationCode, isPtBr: true, [], out var message)
            .Should().BeTrue();

        message.Should().Be(ErrorCopy.All[ErrorCodes.InvalidVerificationCode].PtBr);
        message.Should().NotContainEquivalentOf("remaining attempts");
    }

    [Fact]
    public void TryResolveFormatsThePlaceholdersWithTheResultArguments()
    {
        var formatted = ErrorMessages.MaxTagsPerHabit.Format(5);

        ErrorCopy.TryResolve(formatted.Code, isPtBr: false, formatted.Args, out var english).Should().BeTrue();
        ErrorCopy.TryResolve(formatted.Code, isPtBr: true, formatted.Args, out var portuguese).Should().BeTrue();

        english.Should().Contain("5").And.NotContain("{0}");
        portuguese.Should().Contain("5").And.NotContain("{0}");
    }

    [Fact]
    public void TryResolveReportsAnUnknownCodeRatherThanInventingCopy()
    {
        ErrorCopy.TryResolve("NOT_A_REAL_CODE", isPtBr: false, [], out var message).Should().BeFalse();
        message.Should().BeEmpty();
    }

    [Fact]
    public void AFormattedErrorCarriesItsArgumentsOntoTheResult()
    {
        var result = Result.Failure(ErrorMessages.MaxTagsPerHabit.Format(7));

        result.ErrorCode.Should().Be(ErrorCodes.MaxTagsPerHabit);
        result.ErrorArgs.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public void AnErrorWithoutArgumentsStaysEqualToItself()
    {
        new AppError("SOME_CODE", "Some message.").Should().Be(new AppError("SOME_CODE", "Some message."));
    }

    /// <summary>
    /// <see cref="AppError.Format"/> stores the params array it was handed, and each call allocates
    /// a new one, so the record's generated equality compared two equal errors by reference and
    /// answered false.
    /// </summary>
    [Fact]
    public void TwoErrorsFormattedWithTheSameCountAreEqual()
    {
        var first = ErrorMessages.MaxTagsPerHabit.Format(5);
        var second = ErrorMessages.MaxTagsPerHabit.Format(5);

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Fact]
    public void TwoErrorsFormattedWithDifferentCountsAreNotEqual()
    {
        ErrorMessages.MaxTagsPerHabit.Format(5)
            .Should().NotBe(ErrorMessages.MaxTagsPerHabit.Format(6));
    }

    [Fact]
    public void AFormattedErrorIsNotEqualToItsUnformattedTemplate()
    {
        ErrorMessages.MaxTagsPerHabit.Format(5)
            .Should().NotBe(ErrorMessages.MaxTagsPerHabit);
    }
}
