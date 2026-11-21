namespace AbpLanguages;

/// <summary>
/// The languages to try, in order, when showing something translated: the language asked for, its fallbacks, the
/// site's default, and last the source language every text is written in first. A name kept by language
/// (<c>{ "en": "Open", "ar": "مفتوح" }</c>) is chosen with <see cref="Pick"/>.
/// </summary>
public sealed class LanguageChain
{
    public LanguageChain(IEnumerable<string> codes, string source = AbpLanguagesOptions.DefaultSource)
    {
        List<string> list = [];
        foreach (string code in codes.Append(source))
        {
            if (!string.IsNullOrWhiteSpace(code) && !list.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(code);
            }
        }

        Codes = list;
    }

    /// <summary>The source language alone: for what's stored, which must not depend on who is asking.</summary>
    public static LanguageChain Invariant(string source = AbpLanguagesOptions.DefaultSource) => new([], source);

    /// <summary>The languages in order; always ends with the source language.</summary>
    public IReadOnlyList<string> Codes { get; }

    /// <summary>The language asked for (the chain's first).</summary>
    public string Language => Codes[0];

    /// <summary>
    /// The first non-empty name in the chain's order, else any name at all, else <paramref name="fallback"/> (usually
    /// the key), so something readable always shows.
    /// </summary>
    public string Pick(IReadOnlyDictionary<string, string>? names, string fallback)
    {
        if (names is null || names.Count == 0)
        {
            return fallback;
        }

        foreach (string code in Codes)
        {
            foreach ((string language, string name) in names)
            {
                if (string.Equals(language, code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return names.Values.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? fallback;
    }
}
