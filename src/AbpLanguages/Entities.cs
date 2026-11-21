using Volo.Abp.Domain.Entities;

namespace AbpLanguages;

/// <summary>A language the site offers: any BCP 47 code, with its direction and the language it falls back to.</summary>
public class Language : Entity<Guid>
{
    protected Language()
    {
    }

    public Language(Guid id, string code, string nativeName, string englishName, string direction)
        : base(id)
    {
        Code = code;
        NativeName = nativeName;
        EnglishName = englishName;
        Direction = direction;
        CreatedAt = UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>BCP 47 code: "en", "ar", "fr-CA". Fixed once created; names, texts and cookies refer to it.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>The name in the language itself ("العربية"), shown in the language picker.</summary>
    public string NativeName { get; set; } = null!;

    public string EnglishName { get; set; } = null!;

    /// <summary>"ltr" or "rtl".</summary>
    public string Direction { get; set; } = "ltr";

    public bool Enabled { get; set; } = true;

    public bool IsDefault { get; set; }

    /// <summary>The language to try next when a text is missing in this one ("ar-SA" falls back to "ar").</summary>
    public string? Fallback { get; set; }

    /// <summary>Position in the language picker.</summary>
    public int Order { get; set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One text an admin translated or reworded: it wins over the text shipped with the code. <see cref="SourceHash"/> is a
/// hash of the source text it was made from, so a later change to the source shows it as out of date.
/// </summary>
public class TextOverride : Entity<Guid>
{
    protected TextOverride()
    {
    }

    public TextOverride(Guid id, string language, string catalog, string key, string value, string sourceHash, Guid? updatedBy)
        : base(id)
    {
        Language = language;
        Catalog = catalog;
        Key = key;
        Value = value;
        SourceHash = sourceHash;
        UpdatedBy = updatedBy;
        UpdatedAt = DateTime.UtcNow;
    }

    public string Language { get; private set; } = null!;

    /// <summary>The catalog the text is in: the app's own, or a module's or plugin's.</summary>
    public string Catalog { get; private set; } = null!;

    /// <summary>The text's dotted key, such as "records.create".</summary>
    public string Key { get; private set; } = null!;

    public string Value { get; set; } = null!;

    public string SourceHash { get; set; } = null!;

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One row whose version goes up with every change to languages or texts, in the same transaction. Every server
/// compares it (at most every <see cref="AbpLanguagesOptions.CheckEvery"/>) with the version its caches were built from,
/// so a change on one reaches all of them without a distributed cache.
/// </summary>
public class LanguagesState : Entity<int>
{
    /// <summary>The one row's id.</summary>
    public const int TheId = 1;

    protected LanguagesState()
    {
    }

    public LanguagesState(int id)
        : base(id)
    {
    }

    public long Version { get; set; }
}
