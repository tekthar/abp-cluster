using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpLanguages;

/// <summary>
/// Texts that ship with code: a catalog per owner (the app's own, a module's, a plugin's), each a flat map of dotted
/// keys per language. The source language's texts define the catalog's keys.
/// </summary>
public sealed record TextCatalog(string Name, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts)
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>The texts in a language, as shipped; none when it ships nothing in it.</summary>
    public IReadOnlyDictionary<string, string> In(string language) => Texts.GetValueOrDefault(language) ?? None;

    /// <summary>A text as shipped in a language, or null.</summary>
    public string? Bundled(string language, string key) =>
        Texts.TryGetValue(language, out IReadOnlyDictionary<string, string>? texts) && texts.TryGetValue(key, out string? value) ? value : null;
}

/// <summary>
/// Where catalogs come from: the host's own texts, embedded or on disk, and a plugin system's. Every source registered
/// is read. One whose catalogs change while the site runs (a plugin turned on) gives a <see cref="Generation"/> that
/// changes with them, and the texts are read and built again.
/// </summary>
public interface ITextCatalogSource
{
    IEnumerable<TextCatalog> Load();

    /// <summary>A number that changes when <see cref="Load"/> would give something else; 0 for catalogs that never change.</summary>
    long Generation => 0;
}

/// <summary>
/// Catalogs embedded in an assembly as <c>{prefix}{catalog}.{language}.json</c> (<c>myapp.texts.app.ar.json</c>), each
/// a nested JSON of texts: a web app's <c>messages/*.json</c>, linked into the server's assembly.
/// </summary>
public sealed class EmbeddedTextCatalogSource(Assembly assembly, string prefix) : ITextCatalogSource
{
    public IEnumerable<TextCatalog> Load()
    {
        Dictionary<string, Dictionary<string, IReadOnlyDictionary<string, string>>> catalogs = new(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            string[] parts = resource[prefix.Length..^".json".Length].Split('.');
            if (parts.Length != 2)
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using JsonDocument doc = JsonDocument.Parse(stream);
            if (!catalogs.TryGetValue(parts[0], out Dictionary<string, IReadOnlyDictionary<string, string>>? languages))
            {
                catalogs[parts[0]] = languages = new(StringComparer.OrdinalIgnoreCase);
            }

            languages[parts[1]] = TextJson.Flatten(doc.RootElement);
        }

        return catalogs.Select(c => new TextCatalog(c.Key, c.Value));
    }
}

/// <summary>Texts as JSON, their hashes, and their ICU arguments.</summary>
public static class TextJson
{
    /// <summary>Nested message JSON to a flat map of dotted keys, in document order. Leaves that aren't strings are skipped.</summary>
    public static Dictionary<string, string> Flatten(JsonElement root)
    {
        Dictionary<string, string> flat = new(StringComparer.Ordinal);
        Walk(root, null, flat);
        return flat;
    }

    public static Dictionary<string, string> Flatten(JsonNode? root) =>
        root is null ? new Dictionary<string, string>(StringComparer.Ordinal) : Flatten(JsonSerializer.SerializeToElement(root));

    private static void Walk(JsonElement element, string? prefix, Dictionary<string, string> flat)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Walk(property.Value, prefix is null ? property.Name : $"{prefix}.{property.Name}", flat);
            }
        }
        else if (element.ValueKind == JsonValueKind.String && prefix is not null)
        {
            flat[prefix] = element.GetString()!;
        }
    }

    /// <summary>Dotted keys back to nested JSON, in the order given.</summary>
    public static JsonObject Nest(IEnumerable<KeyValuePair<string, string>> texts)
    {
        JsonObject root = new();
        foreach ((string key, string value) in texts)
        {
            string[] path = key.Split('.');
            JsonObject node = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                if (node[path[i]] is not JsonObject child)
                {
                    node[path[i]] = child = new JsonObject();
                }

                node = child;
            }

            node[path[^1]] = value;
        }

        return root;
    }

    /// <summary>A short hash of a text, kept with a translation to notice when its source changes.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>The ICU arguments a text uses (<c>{name}</c>, <c>{count, plural, …}</c>): a translation must keep the same ones.</summary>
    public static SortedSet<string> Arguments(string text)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                if (depth == 0)
                {
                    int end = i + 1;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                    {
                        end++;
                    }

                    if (end > i + 1)
                    {
                        names.Add(text[(i + 1)..end]);
                    }
                }

                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
            }
        }

        return names;
    }

    /// <summary>What's wrong with a translation's arguments against its source ("keep {name}"), or null when they match.</summary>
    public static string? ArgumentProblem(string source, string value)
    {
        SortedSet<string> expected = Arguments(source), actual = Arguments(value);
        if (expected.SetEquals(actual))
        {
            return null;
        }

        string missing = string.Join(", ", expected.Except(actual).Select(a => "{" + a + "}"));
        string extra = string.Join(", ", actual.Except(expected).Select(a => "{" + a + "}"));
        return (missing.Length > 0 ? $"keep {missing}" : "") + (missing.Length > 0 && extra.Length > 0 ? " and " : "")
            + (extra.Length > 0 ? $"{extra} isn't in the source text" : "");
    }
}
