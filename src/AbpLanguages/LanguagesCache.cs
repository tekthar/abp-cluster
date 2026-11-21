using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpLanguages;

/// <summary>A language as every server reads it.</summary>
public sealed record LanguageInfo(
    string Code, string NativeName, string EnglishName, string Direction, bool Enabled, bool IsDefault, string? Fallback, int Order);

/// <summary>What the site's languages were at one version.</summary>
public sealed record LanguagesSnapshot(long Version, IReadOnlyList<LanguageInfo> Languages, DateTime CheckedAt, string Source)
{
    /// <summary>The default language, else the first, else the source language.</summary>
    public LanguageInfo Default => Languages.FirstOrDefault(l => l.IsDefault)
        ?? (Languages is [var first, ..] ? first : new LanguageInfo(Source, Source, Source, "ltr", true, true, null, 0));

    public LanguageInfo? Find(string code) => Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The chain for an <c>Accept-Language</c> header: the first enabled language it names (exactly, by its base language,
    /// or a regional language of that base), in order of preference, else the site's default.
    /// </summary>
    public LanguageChain ChainForHeader(string? acceptLanguage)
    {
        List<LanguageInfo> enabled = [.. Languages.Where(l => l.Enabled)];
        foreach (string tag in Tags(acceptLanguage))
        {
            string baseTag = tag.Split('-')[0];
            LanguageInfo? match = enabled.FirstOrDefault(l => l.Code.Equals(tag, StringComparison.OrdinalIgnoreCase))
                ?? enabled.FirstOrDefault(l => l.Code.Equals(baseTag, StringComparison.OrdinalIgnoreCase))
                ?? enabled.FirstOrDefault(l => l.Code.Split('-')[0].Equals(baseTag, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return ChainFor(match);
            }
        }

        return ChainFor(Default);
    }

    /// <summary>The language, its fallbacks in turn, then the default (the source language is added by <see cref="LanguageChain"/>).</summary>
    public LanguageChain ChainFor(LanguageInfo language)
    {
        List<string> codes = [language.Code];
        string? next = language.Fallback;
        while (next is not null && Find(next) is { } fallback && !codes.Contains(fallback.Code, StringComparer.OrdinalIgnoreCase))
        {
            codes.Add(fallback.Code);
            next = fallback.Fallback;
        }

        codes.Add(Default.Code);
        return new LanguageChain(codes, Source);
    }

    private static IEnumerable<string> Tags(string? header) =>
        (header ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((part, index) =>
            {
                string[] pieces = part.Split(';', StringSplitOptions.TrimEntries);
                double q = 1;
                foreach (string piece in pieces.Skip(1))
                {
                    if (piece.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(piece[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        q = parsed;
                    }
                }

                return (Tag: pieces[0], Q: q, Index: index);
            })
            .Where(t => t.Tag.Length > 0 && t.Tag != "*" && t.Q > 0)
            .OrderByDescending(t => t.Q).ThenBy(t => t.Index)
            .Select(t => t.Tag);
}

/// <summary>A language's texts, merged and serialised once: the JSON a web app loads, and its version (a content hash).</summary>
public sealed record TextBundle(string Language, string Hash, byte[] Json);

/// <summary>One text with its own wording in each enabled language (no fallbacks), for a host's translation search.</summary>
public sealed record TextTranslations(string Catalog, string Key, IReadOnlyDictionary<string, string> Texts);

/// <summary>What goes into a language's bundle: the language, its chain, the texts' version, and each catalog's merged texts, nested.</summary>
public sealed record TextBundleParts(LanguageInfo Language, LanguageChain Chain, string Version, string MainCatalog, IReadOnlyList<KeyValuePair<string, JsonObject>> Catalogs);

/// <summary>
/// The bundle's JSON, which the host's web reads. The default: <c>{ language, direction, chain, version, messages,
/// catalogs }</c>, with the main catalog as <c>messages</c> and every other one under <c>catalogs</c> by its name.
/// </summary>
public interface ITextBundleWriter
{
    byte[] Write(TextBundleParts parts);
}

internal sealed class DefaultTextBundleWriter : ITextBundleWriter
{
    public byte[] Write(TextBundleParts parts)
    {
        JsonObject others = new();
        JsonObject messages = new();
        foreach ((string name, JsonObject texts) in parts.Catalogs)
        {
            if (name == parts.MainCatalog)
            {
                messages = (JsonObject)texts.DeepClone();
            }
            else
            {
                others[name] = texts.DeepClone();
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["language"] = parts.Language.Code,
            ["direction"] = parts.Language.Direction,
            ["chain"] = new JsonArray([.. parts.Chain.Codes.Select(c => (JsonNode)c)]),
            ["version"] = parts.Version,
            ["messages"] = messages,
            ["catalogs"] = others,
        });
    }
}

/// <summary>
/// The site's languages and each language's merged texts, cached on every server:
/// <list type="bullet">
/// <item>Each language's bundle (shipped texts, admins' texts, fallbacks applied) is built once and kept as bytes with a
/// content hash: serving it is a byte copy, and the hash makes a versioned address the browser can cache for good.</item>
/// <item>Servers agree through one row's version, read at most every <see cref="AbpLanguagesOptions.CheckEvery"/>. A
/// change bumps it in its own transaction, and every server then builds what it serves again.</item>
/// <item>Catalogs follow their sources' <see cref="ITextCatalogSource.Generation"/>.</item>
/// </list>
/// </summary>
public sealed class LanguagesCache(
    IServiceScopeFactory scopes,
    IEnumerable<ITextCatalogSource> sources,
    ITextBundleWriter writer,
    IOptions<AbpLanguagesOptions> options,
    ILogger<LanguagesCache> logger) : ISingletonDependency
{
    private readonly AbpLanguagesOptions _options = options.Value;
    private readonly ITextCatalogSource[] _sources = [.. sources];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _catalogLock = new();
    private readonly ConcurrentDictionary<string, (long Version, long Generation, TextBundle Bundle)> _bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Version, IReadOnlyDictionary<(string Catalog, string Key), (string Value, string SourceHash)> Texts)> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    private (long Version, long Generation, IReadOnlyList<TextTranslations> Texts)? _translations;
    private volatile LanguagesSnapshot? _snapshot;
    private (long[] Generations, IReadOnlyList<TextCatalog>[] Loaded, IReadOnlyList<TextCatalog> All)? _catalogs;

    /// <summary>The source language, as the options say.</summary>
    public string Source => _options.Source;

    /// <summary>The last snapshot, without waiting; null only before the first read after start.</summary>
    public LanguagesSnapshot? Current => _snapshot;

    /// <summary>The catalogs' generation: changes whenever a source's does.</summary>
    public long Generation => _sources.Aggregate(17L, (hash, s) => unchecked((hash * 31) + s.Generation));

    public async Task<LanguagesSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        LanguagesSnapshot? snapshot = _snapshot;
        if (snapshot is not null && DateTime.UtcNow - snapshot.CheckedAt < _options.CheckEvery)
        {
            return snapshot;
        }

        await _gate.WaitAsync(ct);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && DateTime.UtcNow - snapshot.CheckedAt < _options.CheckEvery)
            {
                return snapshot;
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            LanguagesDb languagesDb = scope.ServiceProvider.GetRequiredService<LanguagesDb>();
            (long version, List<LanguageInfo>? languages) = await languagesDb.RunAsync(async db =>
            {
                long v = await db.LanguagesState.AsNoTracking().Where(s => s.Id == LanguagesState.TheId).Select(s => s.Version).FirstOrDefaultAsync(ct);
                if (snapshot is not null && snapshot.Version == v)
                {
                    return (v, (List<LanguageInfo>?)null);
                }

                List<LanguageInfo> found = await db.Languages.AsNoTracking()
                    .OrderBy(l => l.Order).ThenBy(l => l.Code)
                    .Select(l => new LanguageInfo(l.Code, l.NativeName, l.EnglishName, l.Direction, l.Enabled, l.IsDefault, l.Fallback, l.Order))
                    .ToListAsync(ct);
                return (v, found);
            }, cancellationToken: ct);
            if (languages is null)
            {
                return _snapshot = snapshot! with { CheckedAt = DateTime.UtcNow };
            }

            logger.LogDebug("Languages version {Version}: {Count} languages", version, languages.Count);
            return _snapshot = new LanguagesSnapshot(version, languages, DateTime.UtcNow, _options.Source);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes the next read check the version at once (after this server changed something).</summary>
    public void Invalidate()
    {
        if (_snapshot is { } snapshot)
        {
            _snapshot = snapshot with { CheckedAt = DateTime.MinValue };
        }
    }

    /// <summary>Every source's catalogs: read again for a source whose generation changed. A later source's catalog of the same name wins.</summary>
    public IReadOnlyList<TextCatalog> Catalogs()
    {
        lock (_catalogLock)
        {
            long[] generations = [.. _sources.Select(s => s.Generation)];
            if (_catalogs is { } cached && cached.Generations.AsSpan().SequenceEqual(generations))
            {
                return cached.All;
            }

            IReadOnlyList<TextCatalog>[] loaded = new IReadOnlyList<TextCatalog>[_sources.Length];
            for (int i = 0; i < _sources.Length; i++)
            {
                loaded[i] = _catalogs is { } previous && previous.Generations[i] == generations[i] ? previous.Loaded[i] : [.. _sources[i].Load()];
            }

            Dictionary<string, TextCatalog> byName = new(StringComparer.Ordinal);
            List<string> order = [];
            foreach (TextCatalog catalog in loaded.SelectMany(l => l))
            {
                if (catalog.In(_options.Source).Count == 0)
                {
                    logger.LogWarning("The text catalog {Catalog} has no texts in {Source}, the source language; it's left out", catalog.Name, _options.Source);
                    continue;
                }

                if (!byName.ContainsKey(catalog.Name))
                {
                    order.Add(catalog.Name);
                }

                byName[catalog.Name] = catalog;
            }

            List<TextCatalog> all = [.. order.Select(n => byName[n])];
            _catalogs = (generations, loaded, all);
            return all;
        }
    }

    /// <summary>A catalog by its name, or null.</summary>
    public TextCatalog? Catalog(string name) => Catalogs().FirstOrDefault(c => c.Name == name);

    /// <summary>One language's admin texts, cached per version.</summary>
    public async Task<IReadOnlyDictionary<(string Catalog, string Key), (string Value, string SourceHash)>> OverridesAsync(
        LanguagesSnapshot snapshot, string language, CancellationToken ct = default)
    {
        if (_overrides.TryGetValue(language, out var cached) && cached.Version == snapshot.Version)
        {
            return cached.Texts;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        Dictionary<(string, string), (string, string)> texts = await scope.ServiceProvider.GetRequiredService<LanguagesDb>().RunAsync(async db =>
            (await db.TextOverrides.AsNoTracking()
                .Where(t => t.Language == language)
                .Select(t => new { t.Catalog, t.Key, t.Value, t.SourceHash })
                .ToListAsync(ct))
            .ToDictionary(t => (t.Catalog, t.Key), t => (t.Value, t.SourceHash)), cancellationToken: ct);
        _overrides[language] = (snapshot.Version, texts);
        return texts;
    }

    /// <summary>
    /// A language's merged texts: for every key of every catalog, the first of the admin's text and the shipped one
    /// along the language's chain, else the source. Null for a language the site doesn't have.
    /// </summary>
    public async Task<TextBundle?> BundleAsync(string code, CancellationToken ct = default)
    {
        LanguagesSnapshot snapshot = await SnapshotAsync(ct);
        if (snapshot.Find(code) is not { } language)
        {
            return null;
        }

        long generation = Generation;
        if (_bundles.TryGetValue(language.Code, out var cached) && cached.Version == snapshot.Version && cached.Generation == generation)
        {
            return cached.Bundle;
        }

        LanguageChain chain = snapshot.ChainFor(language);
        List<IReadOnlyDictionary<(string, string), (string Value, string)>> overrides = [];
        foreach (string c in chain.Codes)
        {
            overrides.Add(await OverridesAsync(snapshot, c, ct));
        }

        List<KeyValuePair<string, JsonObject>> catalogs = [];
        JsonObject all = new();
        foreach (TextCatalog catalog in Catalogs())
        {
            IReadOnlyDictionary<string, string> source = catalog.In(_options.Source);
            List<KeyValuePair<string, string>> texts = new(source.Count);
            foreach ((string key, string text) in source)
            {
                string? value = null;
                for (int i = 0; i < chain.Codes.Count && value is null; i++)
                {
                    value = overrides[i].TryGetValue((catalog.Name, key), out var o) ? o.Value : catalog.Bundled(chain.Codes[i], key);
                }

                texts.Add(new(key, value ?? text));
            }

            JsonObject nested = TextJson.Nest(texts);
            catalogs.Add(new(catalog.Name, nested));
            all[catalog.Name] = nested.DeepClone();
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(all)))[..16];
        TextBundle bundle = new(language.Code, hash, writer.Write(new TextBundleParts(language, chain, hash, _options.MainCatalog, catalogs)));
        _bundles[language.Code] = (snapshot.Version, generation, bundle);
        return bundle;
    }

    /// <summary>
    /// One text along a chain, as the bundles have it: the admin's text, else the shipped one, in each language in turn,
    /// else the source. Null when the catalog has no such key.
    /// </summary>
    public async Task<string?> TextAsync(string catalog, string key, LanguageChain chain, CancellationToken ct = default)
    {
        if (Catalog(catalog) is not { } found || !found.In(_options.Source).TryGetValue(key, out string? source))
        {
            return null;
        }

        LanguagesSnapshot snapshot = await SnapshotAsync(ct);
        foreach (string code in chain.Codes)
        {
            string? value = (await OverridesAsync(snapshot, code, ct)).TryGetValue((catalog, key), out var o) ? o.Value : found.Bundled(code, key);
            if (value is not null)
            {
                return value;
            }
        }

        return source;
    }

    /// <summary>
    /// Every text with its own wording in each enabled language: the admin's text, else the shipped one (the source
    /// language always has its source). Built once per version and generation, for a host's translation search.
    /// </summary>
    public async Task<IReadOnlyList<TextTranslations>> TranslationsAsync(CancellationToken ct = default)
    {
        LanguagesSnapshot snapshot = await SnapshotAsync(ct);
        long generation = Generation;
        if (_translations is { } cached && cached.Version == snapshot.Version && cached.Generation == generation)
        {
            return cached.Texts;
        }

        List<LanguageInfo> enabled = [.. snapshot.Languages.Where(l => l.Enabled)];
        Dictionary<string, IReadOnlyDictionary<(string Catalog, string Key), (string Value, string SourceHash)>> overrides = new(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageInfo language in enabled)
        {
            overrides[language.Code] = await OverridesAsync(snapshot, language.Code, ct);
        }

        List<TextTranslations> texts = [];
        foreach (TextCatalog catalog in Catalogs())
        {
            foreach ((string key, string source) in catalog.In(_options.Source))
            {
                Dictionary<string, string> own = new(StringComparer.OrdinalIgnoreCase);
                foreach (LanguageInfo language in enabled)
                {
                    string? value = overrides[language.Code].TryGetValue((catalog.Name, key), out var o) ? o.Value
                        : string.Equals(language.Code, _options.Source, StringComparison.OrdinalIgnoreCase) ? source : catalog.Bundled(language.Code, key);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        own[language.Code] = value;
                    }
                }

                own.TryAdd(_options.Source, source);
                texts.Add(new TextTranslations(catalog.Name, key, own));
            }
        }

        _translations = (snapshot.Version, generation, texts);
        return texts;
    }

    /// <summary>The chain for an <c>Accept-Language</c> header, against the languages as this server last read them; the source alone before the first read.</summary>
    public LanguageChain ChainFor(string? acceptLanguage) =>
        _snapshot is { Languages.Count: > 0 } snapshot ? snapshot.ChainForHeader(acceptLanguage) : LanguageChain.Invariant(_options.Source);
}
