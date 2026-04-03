using FluentAssertions;
using Orbit.Application.Habits.Services;

namespace Orbit.Application.Tests.Services;

public class FuzzyMatcherTests
{
    // --- LevenshteinDistance tests ---

    [Fact]
    public void LevenshteinDistance_IdenticalStrings_ReturnsZero()
    {
        FuzzyMatcher.LevenshteinDistance("hello", "hello").Should().Be(0);
    }

    [Fact]
    public void LevenshteinDistance_SingleInsertion_ReturnsOne()
    {
        FuzzyMatcher.LevenshteinDistance("helo", "hello").Should().Be(1);
    }

    [Fact]
    public void LevenshteinDistance_SingleDeletion_ReturnsOne()
    {
        FuzzyMatcher.LevenshteinDistance("hello", "helo").Should().Be(1);
    }

    [Fact]
    public void LevenshteinDistance_SingleSubstitution_ReturnsOne()
    {
        FuzzyMatcher.LevenshteinDistance("hello", "hallo").Should().Be(1);
    }

    [Fact]
    public void LevenshteinDistance_TwoEdits_ReturnsTwo()
    {
        FuzzyMatcher.LevenshteinDistance("kitten", "sitten").Should().Be(1);
        FuzzyMatcher.LevenshteinDistance("kitten", "sitting").Should().Be(3);
    }

    [Fact]
    public void LevenshteinDistance_CompletelyDifferent_ReturnsMaxLength()
    {
        FuzzyMatcher.LevenshteinDistance("abc", "xyz").Should().Be(3);
    }

    [Fact]
    public void LevenshteinDistance_EmptyFirst_ReturnsSecondLength()
    {
        FuzzyMatcher.LevenshteinDistance("", "hello").Should().Be(5);
    }

    [Fact]
    public void LevenshteinDistance_EmptySecond_ReturnsFirstLength()
    {
        FuzzyMatcher.LevenshteinDistance("hello", "").Should().Be(5);
    }

    [Fact]
    public void LevenshteinDistance_BothEmpty_ReturnsZero()
    {
        FuzzyMatcher.LevenshteinDistance("", "").Should().Be(0);
    }

    [Fact]
    public void LevenshteinDistance_NullFirst_ReturnsSecondLength()
    {
        FuzzyMatcher.LevenshteinDistance(null!, "hello").Should().Be(5);
    }

    [Fact]
    public void LevenshteinDistance_NullSecond_ReturnsFirstLength()
    {
        FuzzyMatcher.LevenshteinDistance("hello", null!).Should().Be(5);
    }

    [Fact]
    public void LevenshteinDistance_BothNull_ReturnsZero()
    {
        FuzzyMatcher.LevenshteinDistance(null!, null!).Should().Be(0);
    }

    [Fact]
    public void LevenshteinDistance_CaseInsensitive()
    {
        // The implementation uses char.ToLowerInvariant, so case differences cost 0
        FuzzyMatcher.LevenshteinDistance("Hello", "hello").Should().Be(0);
        FuzzyMatcher.LevenshteinDistance("HELLO", "hello").Should().Be(0);
    }

    [Fact]
    public void LevenshteinDistance_SingleChar_Difference()
    {
        FuzzyMatcher.LevenshteinDistance("a", "b").Should().Be(1);
    }

    [Fact]
    public void LevenshteinDistance_SingleChar_Same()
    {
        FuzzyMatcher.LevenshteinDistance("a", "a").Should().Be(0);
    }

    // --- FuzzyContains tests ---

    [Fact]
    public void FuzzyContains_ExactSubstringMatch_ReturnsTrue()
    {
        FuzzyMatcher.FuzzyContains("Daily exercise routine", "exercise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_ExactMatch_CaseInsensitive_ReturnsTrue()
    {
        FuzzyMatcher.FuzzyContains("Daily Exercise", "exercise").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("daily exercise", "EXERCISE").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_CloseMatch_SingleTypo_ReturnsTrue()
    {
        // "exrcise" is 1 edit from "exercise" (missing 'e')
        FuzzyMatcher.FuzzyContains("Daily exercise", "exrcise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_CloseMatch_Substitution_ReturnsTrue()
    {
        // "exertise" is 1 edit from "exercise"
        FuzzyMatcher.FuzzyContains("Daily exercise", "exertise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_NoMatch_ReturnsFlase()
    {
        FuzzyMatcher.FuzzyContains("Daily exercise", "programming").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_EmptyText_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains("", "exercise").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_EmptyTerm_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains("Daily exercise", "").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_WhitespaceText_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains("   ", "exercise").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_WhitespaceTerm_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains("Daily exercise", "   ").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_NullText_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains(null!, "exercise").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_NullTerm_ReturnsFalse()
    {
        FuzzyMatcher.FuzzyContains("Daily exercise", null!).Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_ShortTerm_ExactOnly()
    {
        // Terms <= 2 chars use exact match only
        FuzzyMatcher.FuzzyContains("go running", "go").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("go running", "ge").Should().BeFalse(); // 1 edit but short term
    }

    [Fact]
    public void FuzzyContains_SingleCharTerm_ExactSubstringOnly()
    {
        FuzzyMatcher.FuzzyContains("a test", "a").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("b test", "a").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_TwoCharTerm_ExactSubstringOnly()
    {
        FuzzyMatcher.FuzzyContains("go for a run", "go").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("go for a run", "gx").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_MultiWordSearch_AllWordsMustMatch()
    {
        // Both "daily" and "exercise" must match
        FuzzyMatcher.FuzzyContains("Daily exercise routine", "daily exercise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_MultiWordSearch_OneWordMissing_ReturnsFalse()
    {
        // "daily" matches but "cooking" doesn't
        FuzzyMatcher.FuzzyContains("Daily exercise routine", "daily cooking").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_MultiWordSearch_AllFuzzy_ReturnsTrue()
    {
        // "daly" is 1 edit from "daily", "exrcise" is close to "exercise"
        FuzzyMatcher.FuzzyContains("Daily exercise routine", "daly exrcise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_LongSearchWord_AllowsTwoEdits()
    {
        // Words >= 8 chars allow max distance of 2
        // "exercize" (8 chars) is 1 edit from "exercise", should match
        FuzzyMatcher.FuzzyContains("Daily exercise", "exercize").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_LongSearchWord_ThreeEdits_ReturnsFalse()
    {
        // "exarzize" is 3 edits from "exercise", should not match even for long words
        FuzzyMatcher.FuzzyContains("Daily exercise", "exarzize").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_ShortWord_OneEdit_ReturnsTrue()
    {
        // Short words (< 8 chars) allow 1 edit max
        // "ren" is 1 edit from "run" (substitution e->u), should match
        FuzzyMatcher.FuzzyContains("go run now", "ren").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_ShortWord_TwoEdits_ReturnsFalse()
    {
        // Short words (< 8 chars) only allow 1 edit max
        // "xyz" is 3 edits from "run", should not match
        FuzzyMatcher.FuzzyContains("go run now", "xyz").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_WordLengthDifferenceTooLarge_SkipsComparison()
    {
        // If word length differs by more than maxDistance, it's skipped
        FuzzyMatcher.FuzzyContains("hi there", "hello").Should().BeFalse();
    }

    [Theory]
    [InlineData("Read a book", "read", true)]
    [InlineData("Morning meditation", "meditaton", true)] // 1 edit from "meditation"
    [InlineData("Exercise daily", "exercis", true)]
    [InlineData("Walk the dog", "walking", false)] // length difference + edits too many
    [InlineData("Drink water", "drink", true)]
    public void FuzzyContains_VariousScenarios(string text, string term, bool expected)
    {
        FuzzyMatcher.FuzzyContains(text, term).Should().Be(expected);
    }

    // --- Additional edge cases ---

    [Fact]
    public void FuzzyContains_SpecialCharacters_ExactMatch()
    {
        FuzzyMatcher.FuzzyContains("C++ programming", "C++").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_SpecialCharacters_NoMatch()
    {
        FuzzyMatcher.FuzzyContains("C++ programming", "Java").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_MultipleSpacesInTerm_SplitCorrectly()
    {
        // Extra spaces between words
        FuzzyMatcher.FuzzyContains("Daily morning exercise", "daily   exercise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_SingleWordText_ExactMatch()
    {
        FuzzyMatcher.FuzzyContains("exercise", "exercise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_SingleWordText_FuzzyMatch()
    {
        FuzzyMatcher.FuzzyContains("exercise", "exrcise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_SingleWordText_NoMatch()
    {
        FuzzyMatcher.FuzzyContains("exercise", "sleeping").Should().BeFalse();
    }

    [Fact]
    public void LevenshteinDistance_LongStrings_CalculatesCorrectly()
    {
        // "programming" vs "programing" - 1 deletion
        FuzzyMatcher.LevenshteinDistance("programming", "programing").Should().Be(1);
    }

    [Fact]
    public void FuzzyContains_NumbersInText_MatchesExact()
    {
        FuzzyMatcher.FuzzyContains("Run 5km daily", "5km").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_MixedCase_AllVariations()
    {
        FuzzyMatcher.FuzzyContains("DAILY Exercise", "daily exercise").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("daily exercise", "DAILY EXERCISE").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("DaIlY eXeRcIsE", "daily exercise").Should().BeTrue();
    }

    [Fact]
    public void FuzzyContains_ThreeWordSearch_AllMustMatch()
    {
        FuzzyMatcher.FuzzyContains("Morning daily exercise routine", "morning daily exercise").Should().BeTrue();
        FuzzyMatcher.FuzzyContains("Morning daily exercise routine", "morning daily cooking").Should().BeFalse();
    }

    [Fact]
    public void FuzzyContains_ExactSubstring_MiddleOfWord()
    {
        FuzzyMatcher.FuzzyContains("unexercised", "exercise").Should().BeTrue();
    }

    [Theory]
    [InlineData("ab", "ab", 0)]
    [InlineData("ab", "cd", 2)]
    [InlineData("abc", "aec", 1)]
    [InlineData("abc", "abc", 0)]
    public void LevenshteinDistance_ShortStrings(string a, string b, int expected)
    {
        FuzzyMatcher.LevenshteinDistance(a, b).Should().Be(expected);
    }
}
