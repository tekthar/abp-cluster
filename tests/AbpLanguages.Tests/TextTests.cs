using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace AbpLanguages.Tests;

/// <summary>Chains, texts as JSON and their arguments, and the tables' names: what needs no database.</summary>
public sealed class TextTests
{
    private static LanguagesSnapshot Site(params LanguageInfo[] languages) => new(1, languages, DateTime.UtcNow, "en");

    private static LanguageInfo L(string code, bool enabled = true, bool isDefault = false, string? fallback = null) =>
        new(code, code, code, "ltr", enabled, isDefault, fallback, 0);

    [Fact]
    public void A_chain_is_the_language_its_fallbacks_the_default_then_the_source_each_once()
    {
        LanguagesSnapshot site = Site(L("en"), L("fr", isDefault: true), L("ar"), L("ar-SA", fallback: "ar"));
        Assert.Equal(["ar-SA", "ar", "fr", "en"], site.ChainFor(site.Find("ar-SA")!).Codes);
        Assert.Equal(["fr", "en"], site.ChainFor(site.Default).Codes);
        Assert.Equal(["en"], LanguageChain.Invariant().Codes);
    }

    [Fact]
    public void A_header_picks_the_first_enabled_language_exactly_by_its_base_or_a_regional_one_else_the_default()
    {
        LanguagesSnapshot site = Site(L("en", isDefault: true), L("ar"), L("fr-CA"), L("de", enabled: false));
        Assert.Equal("ar", site.ChainForHeader("ar-EG,en;q=0.5").Language);
        Assert.Equal("fr-CA", site.ChainForHeader("fr").Language);
        Assert.Equal("ar", site.ChainForHeader("de, ar;q=0.8").Language);
        Assert.Equal("ar", site.ChainForHeader("en;q=0.1, ar;q=0.9").Language);
        Assert.Equal("en", site.ChainForHeader("ja, *").Language);
        Assert.Equal("en", site.ChainForHeader(null).Language);
    }

    [Fact]
    public void Pick_takes_the_chains_first_name_else_any_else_the_fallback()
    {
        LanguageChain chain = new(["ar", "fr"]);
        Assert.Equal("مفتوح", chain.Pick(new Dictionary<string, string> { ["en"] = "Open", ["ar"] = "مفتوح" }, "open"));
        Assert.Equal("Open", chain.Pick(new Dictionary<string, string> { ["en"] = "Open", ["ar"] = " " }, "open"));
        Assert.Equal("Offen", chain.Pick(new Dictionary<string, string> { ["de"] = "Offen" }, "open"));
        Assert.Equal("open", chain.Pick(null, "open"));
    }

    [Fact]
    public void Texts_flatten_to_dotted_keys_and_nest_back_in_order()
    {
        JsonElement nested = JsonDocument.Parse("""{"a":{"b":"one","c":{"d":"two"}},"e":"three","n":1}""").RootElement;
        Dictionary<string, string> flat = TextJson.Flatten(nested);
        Assert.Equal(["a.b", "a.c.d", "e"], flat.Keys);
        Assert.Equal("""{"a":{"b":"one","c":{"d":"two"}},"e":"three"}""", TextJson.Nest(flat).ToJsonString());
        Assert.Empty(TextJson.Flatten((JsonNode?)null));
    }

    [Fact]
    public void A_translation_keeps_the_sources_arguments()
    {
        Assert.Equal(["count", "name"], TextJson.Arguments("{name} has {count, plural, one {# record} other {# records}}"));
        Assert.Null(TextJson.ArgumentProblem("Hello {name}", "مرحبا {name}"));
        Assert.Equal("keep {name}", TextJson.ArgumentProblem("Hello {name}", "Hello"));
        Assert.Equal("keep {name} and {who} isn't in the source text", TextJson.ArgumentProblem("Hello {name}", "Hello {who}"));
        Assert.Equal(12, TextJson.Hash("Hello").Length);
    }

    [Fact]
    public void The_direction_comes_from_the_code_unless_given()
    {
        Assert.Equal("rtl", LanguagesService.DirectionOf(null, "ar-SA"));
        Assert.Equal("ltr", LanguagesService.DirectionOf("", "fr"));
        Assert.Equal("ltr", LanguagesService.DirectionOf("LTR", "ar"));
        Assert.Throws<Volo.Abp.UserFriendlyException>(() => LanguagesService.DirectionOf("up", "fr"));
    }

    [Fact]
    public void A_host_keeps_its_own_schema_and_table_names()
    {
        using NamedTables context = new(new DbContextOptionsBuilder<NamedTables>().UseNpgsql("Host=localhost").Options);
        Assert.Equal(("platform", "languages"), (context.Model.FindEntityType(typeof(Language))!.GetSchema(), context.Model.FindEntityType(typeof(Language))!.GetTableName()));
        Assert.Equal("localization_state", context.Model.FindEntityType(typeof(LanguagesState))!.GetTableName());
        Assert.Equal("text_overrides", context.Model.FindEntityType(typeof(TextOverride))!.GetTableName());
    }

    private sealed class NamedTables(DbContextOptions<NamedTables> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureLanguages("platform", new LanguagesTables(State: "localization_state"));
    }
}
