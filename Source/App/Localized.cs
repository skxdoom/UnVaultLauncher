using System.Globalization;

namespace UnVault.App;

/// <summary>
/// Interface text from Strings.resx with values put in. Values go by position ({0}, {1}…), so a translation can place
/// them where its grammar needs; numbers and dates are written the user's way.
/// </summary>
internal static class Localized
{
    public static string Format(string format, params object?[] values) => string.Format(CultureInfo.CurrentCulture, format, values);

    /// <summary>
    /// Text that depends on a count, e.g. "1 item" or "5 items". Strings.resx holds one entry per form a language needs,
    /// named X_One, X_Few, X_Many and X_Other; English needs only _One and _Other. The count is {0}, <paramref name="more"/>
    /// values follow as {1}, {2}…
    /// </summary>
    /// <param name="otherForm">The entry's _Other form by nameof, e.g. nameof(Strings.ItemsHaveUpdates_Other), so a misspelt name doesn't build.</param>
    public static string Plural(string otherForm, long count, params object?[] more)
    {
        const string Other = "_Other";
        if (!otherForm.EndsWith(Other, StringComparison.Ordinal))
            throw new ArgumentException($"Expected an _Other entry, not {otherForm}.", nameof(otherForm));
        var culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        string form = Strings.ResourceManager.GetString($"{otherForm[..^Other.Length]}_{Category(count, culture)}", culture)
            ?? Strings.ResourceManager.GetString(otherForm, culture)!;
        return Format(form, [count, .. more]);
    }

    /// <summary>
    /// The form a whole number takes in a language, by the Unicode CLDR plural rules, for languages whose rules differ from
    /// English. A language not listed gets the English choice between One and Other.
    /// </summary>
    internal static string Category(long count, CultureInfo culture)
    {
        long n = Math.Abs(count), last = n % 10, lastTwo = n % 100;
        bool twoToFour = last is >= 2 and <= 4 && lastTwo is < 12 or > 14; // 2–4, 22–24…, but not 12–14
        return culture.TwoLetterISOLanguageName switch
        {
            "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" => "Other",
            "fr" or "pt" => n is 0 or 1 ? "One" : "Other",
            "ru" or "uk" or "be" => last == 1 && lastTwo != 11 ? "One" : twoToFour ? "Few" : "Many",
            "pl" => n == 1 ? "One" : twoToFour ? "Few" : "Many",
            "cs" or "sk" => n == 1 ? "One" : n is >= 2 and <= 4 ? "Few" : "Other",
            _ => n == 1 ? "One" : "Other",
        };
    }
}
