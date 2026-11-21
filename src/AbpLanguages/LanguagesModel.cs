using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpLanguages;

/// <summary>
/// The package's tables, as the host has them in its own context: the host's migrations make them, and its context
/// says it replaces this one (<c>[ReplaceDbContext(typeof(ILanguagesDbContext))]</c>).
/// </summary>
[ConnectionStringName("Default")]
public interface ILanguagesDbContext : IEfCoreDbContext
{
    DbSet<Language> Languages { get; }

    DbSet<TextOverride> TextOverrides { get; }

    DbSet<LanguagesState> LanguagesState { get; }
}

/// <summary>The tables' names, for a host that keeps its own (a product that had them before the package).</summary>
public sealed record LanguagesTables(string Languages = "languages", string TextOverrides = "text_overrides", string State = "state");

/// <summary>The package's model: <c>builder.ConfigureLanguages()</c> in a host context's <c>OnModelCreating</c>.</summary>
public static class LanguagesModel
{
    /// <summary>The package's schema, apart from any host's, unless the host names its own.</summary>
    public const string Schema = "languages";

    public static void ConfigureLanguages(this ModelBuilder builder, string? schema = Schema, LanguagesTables? tables = null)
    {
        tables ??= new LanguagesTables();
        builder.Entity<Language>(b =>
        {
            b.ToTable(tables.Languages, schema);
            b.HasKey(l => l.Id);
            b.HasIndex(l => l.Code).IsUnique();
            b.Property(l => l.Code).HasMaxLength(35).IsRequired();
            b.Property(l => l.NativeName).HasMaxLength(100).IsRequired();
            b.Property(l => l.EnglishName).HasMaxLength(100).IsRequired();
            b.Property(l => l.Direction).HasMaxLength(3).IsRequired();
            b.Property(l => l.Fallback).HasMaxLength(35);
        });

        builder.Entity<TextOverride>(b =>
        {
            b.ToTable(tables.TextOverrides, schema);
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.Language, t.Catalog, t.Key }).IsUnique();
            b.Property(t => t.Language).HasMaxLength(35).IsRequired();
            b.Property(t => t.Catalog).HasMaxLength(129).IsRequired();
            b.Property(t => t.Key).HasMaxLength(256).IsRequired();
            b.Property(t => t.Value).IsRequired();
            b.Property(t => t.SourceHash).HasMaxLength(16).IsRequired();
        });

        builder.Entity<LanguagesState>(b =>
        {
            b.ToTable(tables.State, schema);
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
        });
    }
}

/// <summary>Runs the package's work in a unit of work of its own, so nothing it does waits on a caller's transaction.</summary>
public class LanguagesDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<ILanguagesDbContext> provider) : ITransientDependency
{
#pragma warning disable CA1068
    public async Task<T> RunAsync<T>(Func<ILanguagesDbContext, Task<T>> work, bool isTransactional = false, CancellationToken cancellationToken = default)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: true, isTransactional: isTransactional);
        ILanguagesDbContext db = await provider.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<ILanguagesDbContext, Task> work, bool isTransactional = false, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async db => { await work(db); return true; }, isTransactional, cancellationToken);
#pragma warning restore CA1068
}
