using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UnVault.App.Tests;

public partial class LocalizedTests
{
    /// <summary>
    /// A value an entry takes but the code doesn't give would only fail when the text shows, so this checks every entry, in
    /// English and in each translation: it's a valid format, a text's plural forms take the same values, and a translation
    /// takes no value the English doesn't.
    /// </summary>
    [Fact]
    public void Every_text_is_a_valid_format_with_the_values_English_gives_it()
    {
        var english = Entries(CultureInfo.InvariantCulture)!;
        Assert.NotEmpty(english);
        foreach (var (key, text) in english)
        {
            Assert.Null(Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, new object?[10])));
            if (key.EndsWith("_One", StringComparison.Ordinal))
                Assert.Equal(Values(english[key[..^4] + "_Other"]), Values(text));
        }

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures).Where(c => !c.Equals(CultureInfo.InvariantCulture)))
        {
            if (Entries(culture) is not { } translated)
                continue;
            foreach (var (key, text) in translated)
            {
                // A language's extra plural forms (_Few, _Many) take what the English _Other does.
                string englishKey = Regex.Replace(key, "_(Zero|One|Two|Few|Many)$", "_Other");
                Assert.True(english.ContainsKey(englishKey), $"{culture.Name}: {key} isn't an English entry.");
                Assert.Null(Record.Exception(() => string.Format(culture, text, new object?[10])));
                Assert.Subset(Values(english[englishKey]), Values(text));
            }
        }
    }

    private static Dictionary<string, string>? Entries(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?
            .Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static HashSet<int> Values(string text) => [.. Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    [Theory]
    [InlineData("en", 1, "One")]
    [InlineData("en", 0, "Other")]
    [InlineData("en", 21, "Other")]
    [InlineData("fr", 0, "One")]
    [InlineData("ru", 1, "One")]
    [InlineData("ru", 21, "One")]
    [InlineData("ru", 11, "Many")]
    [InlineData("ru", 3, "Few")]
    [InlineData("ru", 24, "Few")]
    [InlineData("ru", 13, "Many")]
    [InlineData("ru", 5, "Many")]
    [InlineData("pl", 22, "Few")]
    [InlineData("pl", 21, "Many")]
    [InlineData("cs", 4, "Few")]
    [InlineData("ja", 1, "Other")]
    public void Counts_take_the_form_their_language_needs(string language, long count, string expected) =>
        Assert.Equal(expected, Localized.Category(count, new CultureInfo(language)));

    [Fact]
    public void English_has_a_form_for_one_and_one_for_the_rest()
    {
        Strings.Culture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("1 item has an update on Fab", Localized.Plural(nameof(Strings.ItemsHaveUpdates_Other), 1));
            Assert.Equal("5 items have updates on Fab", Localized.Plural(nameof(Strings.ItemsHaveUpdates_Other), 5));
        }
        finally
        {
            Strings.Culture = null;
        }
    }
}
