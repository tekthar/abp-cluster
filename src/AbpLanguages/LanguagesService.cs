using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Users;

namespace AbpLanguages;

public sealed record LanguageDto(
    string Code, string NativeName, string EnglishName, string Direction, bool Enabled, bool IsDefault, string? Fallback, int Order,
    string TextsVersion, int? Translated = null, int? Total = null);

public sealed record LanguagesDto(string DefaultLanguage, List<LanguageDto> Languages);

/// <summary>A new language, or changes to one. Its direction is worked out from its code when left out.</summary>
public sealed record LanguageInput(
    string? Code = null, string? NativeName = null, string? EnglishName = null, string? Direction = null, bool? Enabled = null,
    string? Fallback = null, int? Order = null, bool? IsDefault = null);

/// <summary>
/// One text in the translation editor. Status: <c>source</c> (the source language's, as shipped), <c>translated</c> (an
/// admin's text, made from the current source), <c>outdated</c> (made from a source text that has changed since),
/// <c>bundled</c> (a translation shipped with the code), <c>missing</c> (it falls back).
/// </summary>
public sealed record TextEntryDto(string Catalog, string Key, string Source, string? Bundled, string? Value, string Status);

public sealed record TextEntriesDto(string Language, int Total, int Translated, List<TextEntryDto> Entries);

/// <summary>A text to set; an empty or null value takes the admin's text away (the shipped one or the fallback shows again).</summary>
public sealed record TextChange(string Catalog, string Key, string? Value);

public sealed record SaveTextsInput(List<TextChange> Texts);

public sealed record SaveTextsResult(int Saved, int Removed);

/// <summary>A catalog's texts in nested JSON, as a translator's file has them. Without <c>Overwrite</c>, texts already translated are kept.</summary>
public sealed record ImportTextsInput(string Catalog, JsonObject Messages, bool Overwrite = false);

public sealed record ImportTextsResult(int Imported, int Unchanged, int Kept, List<string> Unknown, List<string> Refused);

/// <summary>What changed, for the host's audit and its own caches: <c>language.created</c>, <c>language.changed</c>, <c>language.removed</c>, <c>texts.saved</c>, <c>texts.imported</c>.</summary>
public sealed record LanguagesChange(string What, string Language, int Count = 0);

/// <summary>Told after every change, once it's committed: the host's audit, a web server's cache to forget. Nothing by default.</summary>
public interface ILanguagesEvents
{
    Task ChangedAsync(LanguagesChange change, CancellationToken cancellationToken);
}

internal sealed class NoLanguagesEvents : ILanguagesEvents
{
    public Task ChangedAsync(LanguagesChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The package's error codes, as an API's answers carry them in <c>error.code</c>.</summary>
public static class LanguagesErrorCodes
{
    /// <summary>What was asked isn't valid (400).</summary>
    public const string Invalid = "AbpLanguages:Invalid";

    /// <summary>The caller may not change languages or texts (403).</summary>
    public const string Forbidden = "AbpLanguages:Forbidden";
}

/// <summary>
/// The site's languages and texts for an admin page: list, add, change and remove a language; the editor's entries;
/// saving texts; a catalog's export and import. Who may call it is the caller's check (AbpLanguages.HttpApi's rights).
/// Every change commits with the version bumped, so every server sees it.
/// </summary>
public partial class LanguagesService(
    LanguagesDb languagesDb,
    LanguagesCache cache,
    ILanguagesEvents events,
    ICurrentUser currentUser,
    IOptions<AbpLanguagesOptions> options) : ITransientDependency
{
    private static readonly HashSet<string> RtlLanguages = new(StringComparer.OrdinalIgnoreCase) { "ar", "fa", "he", "ur", "ps", "ku", "ckb", "dv", "yi", "sd", "ug" };

    private readonly AbpLanguagesOptions _options = options.Value;

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodePattern();

    /// <summary>The direction a language is written in, from its code when none is given.</summary>
    public static string DirectionOf(string? direction, string code) => direction?.Trim().ToLowerInvariant() switch
    {
        "rtl" => "rtl",
        "ltr" => "ltr",
        null or "" => RtlLanguages.Contains(code.Split('-')[0]) ? "rtl" : "ltr",
        _ => throw Invalid("Direction is \"ltr\" or \"rtl\"."),
    };

    /// <summary>The site's languages: the enabled ones, or (<paramref name="all"/>) every one with how much of it is translated.</summary>
    public async Task<LanguagesDto> ListAsync(bool all, CancellationToken ct)
    {
        LanguagesSnapshot snapshot = await cache.SnapshotAsync(ct);
        int total = all ? Total() : 0;
        List<LanguageDto> result = [];
        foreach (LanguageInfo language in snapshot.Languages.Where(l => all || l.Enabled))
        {
            TextBundle bundle = (await cache.BundleAsync(language.Code, ct))!;
            int? translated = all ? await TranslatedCountAsync(snapshot, language.Code, ct) : null;
            result.Add(Dto(language, bundle, translated, all ? total : null));
        }

        return new LanguagesDto(snapshot.Default.Code, result);
    }

    public async Task<LanguageDto> CreateAsync(LanguageInput input, CancellationToken ct)
    {
        string code = (input.Code ?? "").Trim();
        if (code.Length > 35 || !CodePattern().IsMatch(code))
        {
            throw Invalid($"'{code}' isn't a language code: use a BCP 47 code such as \"fr\", \"tr\" or \"fr-CA\".");
        }

        string englishName = Required(input.EnglishName, "English name");
        string nativeName = Required(input.NativeName ?? input.EnglishName, "native name");
        string direction = DirectionOf(input.Direction, code);
        await ChangeAsync(new LanguagesChange("language.created", code), async db =>
        {
            string lower = code.ToLowerInvariant();
            if (await db.Languages.AnyAsync(l => l.Code.ToLower() == lower, ct))
            {
                throw Invalid($"The site already has the language {code}.");
            }

            string? fallback = input.Fallback ?? await BaseLanguageAsync(db, code, ct);
            db.Languages.Add(new Language(Guid.NewGuid(), code, nativeName, englishName, direction)
            {
                Enabled = input.Enabled ?? true,
                Order = input.Order ?? (await db.Languages.MaxAsync(l => (int?)l.Order, ct) ?? 0) + 1,
                Fallback = await FallbackAsync(db, fallback, code, ct),
            });
            return 1;
        }, ct);
        return await DtoAsync(code, ct);
    }

    public async Task<LanguageDto> UpdateAsync(string code, LanguageInput input, CancellationToken ct)
    {
        string found = code;
        await ChangeAsync(new LanguagesChange("language.changed", code), async db =>
        {
            Language language = await FindAsync(db, code, ct);
            found = language.Code;
            if (input.NativeName is not null)
            {
                language.NativeName = Required(input.NativeName, "native name");
            }

            if (input.EnglishName is not null)
            {
                language.EnglishName = Required(input.EnglishName, "English name");
            }

            if (input.Direction is not null)
            {
                language.Direction = DirectionOf(input.Direction, language.Code);
            }

            if (input.Order is not null)
            {
                language.Order = input.Order.Value;
            }

            if (input.Fallback is not null)
            {
                language.Fallback = await FallbackAsync(db, input.Fallback, language.Code, ct);
            }

            if (input.IsDefault == true && !language.IsDefault)
            {
                foreach (Language other in await db.Languages.Where(l => l.IsDefault).ToListAsync(ct))
                {
                    other.IsDefault = false;
                }

                language.IsDefault = true;
                language.Enabled = true;
            }

            if (input.Enabled is not null)
            {
                if (!input.Enabled.Value && language.IsDefault)
                {
                    throw Invalid($"{language.EnglishName} is the default language: make another language the default before turning it off.");
                }

                language.Enabled = input.Enabled.Value;
            }

            language.UpdatedAt = DateTime.UtcNow;
            return 1;
        }, ct);
        return await DtoAsync(found, ct);
    }

    /// <summary>Takes a language away with its admins' texts. Names already given in it stay in the host's data.</summary>
    public async Task DeleteAsync(string code, CancellationToken ct) =>
        await ChangeAsync(new LanguagesChange("language.removed", code), async db =>
        {
            Language language = await FindAsync(db, code, ct);
            if (language.IsDefault)
            {
                throw Invalid($"{language.EnglishName} is the default language: make another language the default first.");
            }

            if (string.Equals(language.Code, _options.Source, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid($"{language.EnglishName} can be turned off but not removed: every text is written in it first.");
            }

            foreach (Language dependent in await db.Languages.Where(l => l.Fallback == language.Code).ToListAsync(ct))
            {
                dependent.Fallback = null;
            }

            db.TextOverrides.RemoveRange(await db.TextOverrides.Where(t => t.Language == language.Code).ToListAsync(ct));
            db.Languages.Remove(language);
            return 1;
        }, ct);

    /// <summary>Every text of every catalog in one language, with its source and status, for the translation editor.</summary>
    public async Task<TextEntriesDto> EntriesAsync(string code, CancellationToken ct)
    {
        LanguagesSnapshot snapshot = await cache.SnapshotAsync(ct);
        LanguageInfo language = snapshot.Find(code) ?? throw new EntityNotFoundException(typeof(Language), code);
        bool isSource = IsSource(language.Code);
        var overrides = await cache.OverridesAsync(snapshot, language.Code, ct);
        List<TextEntryDto> entries = [];
        foreach (TextCatalog catalog in cache.Catalogs())
        {
            foreach ((string key, string source) in catalog.In(_options.Source))
            {
                string? bundled = isSource ? null : catalog.Bundled(language.Code, key);
                string status;
                string? value = null;
                if (overrides.TryGetValue((catalog.Name, key), out var o))
                {
                    value = o.Value;
                    status = o.SourceHash == TextJson.Hash(source) ? "translated" : "outdated";
                }
                else
                {
                    status = isSource ? "source" : bundled is not null ? "bundled" : "missing";
                }

                entries.Add(new TextEntryDto(catalog.Name, key, source, bundled, value, status));
            }
        }

        return new TextEntriesDto(language.Code, entries.Count, entries.Count(e => e.Status is "translated" or "bundled" or "source"), entries);
    }

    /// <summary>Sets or takes away admins' texts, all or none: one that isn't a text, or breaks its source's arguments, saves nothing.</summary>
    public async Task<SaveTextsResult> SaveTextsAsync(string code, SaveTextsInput input, CancellationToken ct)
    {
        Dictionary<string, TextCatalog> catalogs = cache.Catalogs().ToDictionary(c => c.Name, StringComparer.Ordinal);
        List<string> problems = [];
        foreach (TextChange change in input.Texts ?? [])
        {
            if (!catalogs.TryGetValue(change.Catalog, out TextCatalog? catalog) || !catalog.In(_options.Source).TryGetValue(change.Key, out string? source))
            {
                problems.Add($"{change.Catalog}:{change.Key} isn't a text");
            }
            else if (!string.IsNullOrWhiteSpace(change.Value) && TextJson.ArgumentProblem(source, change.Value) is { } problem)
            {
                problems.Add($"{change.Key}: {problem}");
            }
        }

        if (problems.Count > 0)
        {
            throw Invalid("Nothing was saved. " + string.Join("; ", problems.Take(10)) + (problems.Count > 10 ? $"; and {problems.Count - 10} more." : "."));
        }

        int saved = 0, removed = 0;
        await ChangeAsync(new LanguagesChange("texts.saved", code, input.Texts?.Count ?? 0), async db =>
        {
            Language language = await FindAsync(db, code, ct);
            Dictionary<(string, string), TextOverride> existing = await ExistingAsync(db, language.Code, ct);
            foreach (TextChange change in input.Texts ?? [])
            {
                string source = catalogs[change.Catalog].In(_options.Source)[change.Key];
                if (Apply(db, language.Code, change.Catalog, change.Key, change.Value, source, existing) is { } outcome)
                {
                    _ = outcome ? saved++ : removed++;
                }
            }

            return saved + removed;
        }, ct);
        return new SaveTextsResult(saved, removed);
    }

    /// <summary>One catalog's texts in one language as nested JSON, as a translator gets them: only this language's own texts, no fallbacks.</summary>
    public async Task<JsonObject> ExportAsync(string code, string? catalogName, CancellationToken ct)
    {
        LanguagesSnapshot snapshot = await cache.SnapshotAsync(ct);
        LanguageInfo language = snapshot.Find(code) ?? throw new EntityNotFoundException(typeof(Language), code);
        string name = catalogName ?? _options.MainCatalog;
        TextCatalog catalog = cache.Catalog(name) ?? throw Invalid($"There's no text catalog '{name}'.");
        var overrides = await cache.OverridesAsync(snapshot, language.Code, ct);
        IReadOnlyDictionary<string, string> source = catalog.In(_options.Source);
        return TextJson.Nest(source.Keys
            .Select(key => (Key: key, Value: overrides.TryGetValue((name, key), out var o) ? o.Value : IsSource(language.Code) ? source[key] : catalog.Bundled(language.Code, key)))
            .Where(t => t.Value is not null)
            .Select(t => new KeyValuePair<string, string>(t.Key, t.Value!)));
    }

    /// <summary>Takes a translator's file: texts that differ from what ships become admins' texts; unknown keys are listed, never stored.</summary>
    public async Task<ImportTextsResult> ImportAsync(string code, ImportTextsInput input, CancellationToken ct)
    {
        TextCatalog catalog = cache.Catalog(input.Catalog) ?? throw Invalid($"There's no text catalog '{input.Catalog}'.");
        IReadOnlyDictionary<string, string> sources = catalog.In(_options.Source);
        Dictionary<string, string> incoming = TextJson.Flatten(input.Messages);
        int imported = 0, unchanged = 0, kept = 0;
        List<string> unknown = [], refused = [];
        await ChangeAsync(new LanguagesChange("texts.imported", code), async db =>
        {
            Language language = await FindAsync(db, code, ct);
            Dictionary<(string, string), TextOverride> existing = await ExistingAsync(db, language.Code, ct);
            foreach ((string key, string value) in incoming)
            {
                if (!sources.TryGetValue(key, out string? source))
                {
                    unknown.Add(key);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value) || TextJson.ArgumentProblem(source, value) is not null)
                {
                    refused.Add(key);
                    continue;
                }

                string? shipped = IsSource(language.Code) ? source : catalog.Bundled(language.Code, key);
                bool has = existing.TryGetValue((catalog.Name, key), out TextOverride? current);
                if (has && !input.Overwrite)
                {
                    kept++;
                }
                else if ((has ? current!.Value : shipped) == value)
                {
                    unchanged++;
                }
                else
                {
                    Apply(db, language.Code, catalog.Name, key, value == shipped ? null : value, source, existing);
                    imported++;
                }
            }

            return imported;
        }, ct);
        return new ImportTextsResult(imported, unchanged, kept, unknown, refused);
    }

    /// <summary>
    /// One change: in a transaction with the version bumped, so every server builds its caches again; then this server
    /// reads the version at once, and the host is told.
    /// </summary>
    private async Task ChangeAsync(LanguagesChange change, Func<ILanguagesDbContext, Task<int>> work, CancellationToken ct)
    {
        int count = await languagesDb.RunAsync(async db =>
        {
            int n = await work(db);
            await db.SaveChangesAsync(ct);
            await db.LanguagesState.Where(s => s.Id == LanguagesState.TheId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
            return n;
        }, isTransactional: true, ct);
        cache.Invalidate();
        await events.ChangedAsync(change.Count == 0 ? change with { Count = count } : change, ct);
    }

    /// <summary>Sets or takes away one admin's text; true when set, false when taken away, null when nothing changed.</summary>
    private bool? Apply(ILanguagesDbContext db, string language, string catalog, string key, string? value, string source, Dictionary<(string, string), TextOverride> existing)
    {
        existing.TryGetValue((catalog, key), out TextOverride? row);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (row is null)
            {
                return null;
            }

            db.TextOverrides.Remove(row);
            existing.Remove((catalog, key));
            return false;
        }

        if (row is null)
        {
            row = new TextOverride(Guid.NewGuid(), language, catalog, key, value, TextJson.Hash(source), currentUser.Id);
            db.TextOverrides.Add(row);
            existing[(catalog, key)] = row;
        }
        else
        {
            row.Value = value;
            row.SourceHash = TextJson.Hash(source);
            row.UpdatedBy = currentUser.Id;
            row.UpdatedAt = DateTime.UtcNow;
        }

        return true;
    }

    private bool IsSource(string code) => string.Equals(code, _options.Source, StringComparison.OrdinalIgnoreCase);

    private int Total() => cache.Catalogs().Sum(c => c.In(_options.Source).Count);

    private async Task<int> TranslatedCountAsync(LanguagesSnapshot snapshot, string code, CancellationToken ct)
    {
        if (IsSource(code))
        {
            return Total();
        }

        var overrides = await cache.OverridesAsync(snapshot, code, ct);
        return cache.Catalogs().Sum(c =>
        {
            IReadOnlyDictionary<string, string> source = c.In(_options.Source);
            return source.Keys.Count(k => (overrides.TryGetValue((c.Name, k), out var o) && o.SourceHash == TextJson.Hash(source[k]))
                || (!overrides.ContainsKey((c.Name, k)) && c.Bundled(code, k) is not null));
        });
    }

    private async Task<LanguageDto> DtoAsync(string code, CancellationToken ct)
    {
        LanguagesSnapshot snapshot = await cache.SnapshotAsync(ct);
        LanguageInfo language = snapshot.Find(code) ?? throw new EntityNotFoundException(typeof(Language), code);
        TextBundle bundle = (await cache.BundleAsync(language.Code, ct))!;
        return Dto(language, bundle, await TranslatedCountAsync(snapshot, language.Code, ct), Total());
    }

    private static LanguageDto Dto(LanguageInfo l, TextBundle bundle, int? translated, int? total) =>
        new(l.Code, l.NativeName, l.EnglishName, l.Direction, l.Enabled, l.IsDefault, l.Fallback, l.Order, bundle.Hash, translated, total);

    private static async Task<Dictionary<(string, string), TextOverride>> ExistingAsync(ILanguagesDbContext db, string language, CancellationToken ct) =>
        (await db.TextOverrides.Where(t => t.Language == language).ToListAsync(ct)).ToDictionary(t => (t.Catalog, t.Key));

    private static async Task<Language> FindAsync(ILanguagesDbContext db, string code, CancellationToken ct)
    {
        string lower = code.Trim().ToLowerInvariant();
        return await db.Languages.FirstOrDefaultAsync(l => l.Code.ToLower() == lower, ct) ?? throw new EntityNotFoundException(typeof(Language), code);
    }

    private static async Task<string?> FallbackAsync(ILanguagesDbContext db, string? fallback, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return null;
        }

        string lower = fallback.Trim().ToLowerInvariant();
        Language target = await db.Languages.FirstOrDefaultAsync(l => l.Code.ToLower() == lower, ct)
            ?? throw Invalid($"The fallback {fallback} isn't one of the site's languages.");
        string? next = target.Code;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { code };
        while (next is not null)
        {
            if (!seen.Add(next))
            {
                throw Invalid($"Falling back to {target.Code} would make a loop back to {code}.");
            }

            string current = next;
            next = (await db.Languages.AsNoTracking().FirstOrDefaultAsync(l => l.Code == current, ct))?.Fallback;
        }

        return target.Code;
    }

    /// <summary>A regional code ("fr-CA") falls back to its base language ("fr") when the site has it.</summary>
    private static async Task<string?> BaseLanguageAsync(ILanguagesDbContext db, string code, CancellationToken ct)
    {
        if (!code.Contains('-'))
        {
            return null;
        }

        string baseCode = code.Split('-')[0].ToLowerInvariant();
        return await db.Languages.AnyAsync(l => l.Code.ToLower() == baseCode, ct) ? baseCode : null;
    }

    private static string Required(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw Invalid($"Give the language's {what}.") : value.Trim();

    private static UserFriendlyException Invalid(string message) => new(message, LanguagesErrorCodes.Invalid);
}
