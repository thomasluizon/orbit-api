using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Tests.Email;

/// <summary>
/// The ticket asks for a grep over the email copy for dash characters and exclamation marks.
/// A grep only holds for the run it was typed in, so the same check lives here instead, over
/// every string every transactional email can render, in both languages.
/// </summary>
public class EmailCopyTests
{
    private const string DoubledHyphen = "--";
    /// <summary>
    /// The two banned dashes are built from their code points instead of typed, because a file
    /// that detects a dash by embedding one still embeds one, and the Dash Ban gate scans this
    /// file like any other. U+2014 is the em dash and U+2013 is the en dash.
    /// </summary>
    private static readonly string EmDash = ((char)0x2014).ToString();

    private static readonly string EnDash = ((char)0x2013).ToString();

    private static IEnumerable<(string Field, string Value)> StringsOf(object copy) =>
        copy.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (property.Name, (string)property.GetValue(copy)!));

    private static IEnumerable<(string Email, string Field, string Value)> EveryString(bool isPtBr)
    {
        var sources = new (string Email, object Copy)[]
        {
            ("VerificationCode", EmailCopy.VerificationCode(isPtBr)),
            ("Welcome", EmailCopy.Welcome(isPtBr, "Thomas")),
            ("AccountDeletion", EmailCopy.AccountDeletion(isPtBr)),
            ("ApiKeyCreation", EmailCopy.ApiKeyCreation(isPtBr)),
            ("WaitlistConfirmation", EmailCopy.WaitlistConfirmation(isPtBr)),
            ("Support", EmailCopy.Support()),
        };

        foreach (var (email, copy) in sources)
            foreach (var (field, value) in StringsOf(copy))
                yield return (email, field, value);
    }

    public static TheoryData<bool> BothLanguages() => new() { false, true };

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void NoEmailStringCarriesAnExclamationMark(bool isPtBr)
    {
        foreach (var (email, field, value) in EveryString(isPtBr))
            value.Should().NotContain("!", $"{email}.{field} must stay calm");
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void NoEmailStringCarriesADashCharacter(bool isPtBr)
    {
        foreach (var (email, field, value) in EveryString(isPtBr))
        {
            value.Should().NotContain(EmDash, $"{email}.{field} must carry no em dash");
            value.Should().NotContain(EnDash, $"{email}.{field} must carry no en dash");
            value.Should().NotContain(DoubledHyphen, $"{email}.{field} must carry no doubled hyphen");
        }
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void NoEmailStringIsEmpty(bool isPtBr)
    {
        foreach (var (email, field, value) in EveryString(isPtBr))
            value.Should().NotBeNullOrWhiteSpace($"{email}.{field} needs copy");
    }

    [Fact]
    public void EveryLocalizedEmailDiffersBetweenTheTwoLanguages()
    {
        var english = EveryString(isPtBr: false)
            .Where(s => s.Email != "Support")
            .ToDictionary(s => (s.Email, s.Field), s => s.Value);
        var portuguese = EveryString(isPtBr: true)
            .Where(s => s.Email != "Support")
            .ToDictionary(s => (s.Email, s.Field), s => s.Value);

        portuguese.Keys.Should().BeEquivalentTo(english.Keys);

        foreach (var ((email, field), value) in english)
            portuguese[(email, field)].Should().NotBe(
                value, $"{email}.{field} must be translated, not copied");
    }

    /// <summary>
    /// Confirming deletion deactivates the account: <c>ConfirmAccountDeletionCommandHandler</c>
    /// schedules removal at most <see cref="AppConstants.MaxDeletionGraceDays"/> days out, and
    /// signing in again cancels it through <c>User.CancelDeactivation</c>. Copy that promised an
    /// immediate and permanent wipe was wrong in both directions, which is the defect pull request
    /// 954 already corrected once inside the app.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void TheDeletionEmailNamesTheGraceWindowAndTheWayOutOfIt(bool isPtBr)
    {
        var intro = EmailCopy.AccountDeletion(isPtBr).Intro;

        intro.Should().Contain(
            AppConstants.MaxDeletionGraceDays.ToString(CultureInfo.InvariantCulture),
            "the window is read from the constant the handler schedules against");
        intro.Should().ContainEquivalentOf(
            isPtBr ? "Entre de novo" : "Sign in again",
            "signing in cancels the deletion");
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void TheDeletionEmailNeverCallsTheRequestIrreversible(bool isPtBr)
    {
        var intro = EmailCopy.AccountDeletion(isPtBr).Intro;

        foreach (var claim in new[]
                 {
                     "cannot be undone", "não pode ser desfeito",
                     "irreversible", "irreversível", "permanently now", "imediatamente",
                 })
        {
            intro.Should().NotContainEquivalentOf(claim);
        }
    }

    [Fact]
    public void BrandNamesAreNeverTranslated()
    {
        var portuguese = EveryString(isPtBr: true).Select(s => s.Value).ToList();

        portuguese.Should().NotContain(value => value.Contains("Órbita", StringComparison.Ordinal));
        portuguese.Where(value => value.Contains("Orbit", StringComparison.Ordinal)).Should().NotBeEmpty();
    }

    [Fact]
    public void TheSupportRelayIsNotLocalized()
    {
        EmailCopy.Support().Heading.Should().Be("Support request");
        EmailCopy.Support().Footer.Should().NotContainEquivalentOf("the user");
    }
}
