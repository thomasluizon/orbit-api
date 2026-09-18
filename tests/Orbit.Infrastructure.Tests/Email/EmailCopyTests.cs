using System.Reflection;
using FluentAssertions;
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
    private const string EmDash = "—";
    private const string EnDash = "–";

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
        var english = EveryString(isPtBr: false).Where(s => s.Email != "Support").ToList();
        var portuguese = EveryString(isPtBr: true).Where(s => s.Email != "Support").ToList();

        for (var i = 0; i < english.Count; i++)
            portuguese[i].Value.Should().NotBe(
                english[i].Value, $"{english[i].Email}.{english[i].Field} must be translated, not copied");
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
