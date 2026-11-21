namespace AbpLanguages;

/// <summary>The package's settings, in the host's <c>ConfigureServices</c> (<c>Configure&lt;AbpLanguagesOptions&gt;</c>).</summary>
public sealed class AbpLanguagesOptions
{
    /// <summary>English, the language texts are written in first.</summary>
    public const string DefaultSource = "en";

    /// <summary>
    /// The source language: every catalog's keys are its texts' keys, every chain ends with it, and it can be turned
    /// off but not removed.
    /// </summary>
    public string Source { get; set; } = DefaultSource;

    /// <summary>
    /// The catalog that is the app's own texts. The default bundle writer puts it under <c>messages</c> and every other
    /// catalog under <c>catalogs</c>.
    /// </summary>
    public string MainCatalog { get; set; } = "app";

    /// <summary>The languages a new site starts with, in order; the first is the default. English alone unless the host names more.</summary>
    public List<LanguageSeed> Seed { get; } = [new("en", "English", "English")];

    /// <summary>How often a server reads the version every server shares, at most.</summary>
    public TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>A language a new site starts with. Its direction is worked out from its code when left out.</summary>
public sealed record LanguageSeed(string Code, string NativeName, string EnglishName, string? Direction = null, string? Fallback = null);
