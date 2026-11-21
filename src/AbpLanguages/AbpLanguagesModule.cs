using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Volo.Abp.Users;

namespace AbpLanguages;

/// <summary>
/// A site's languages and translations: <c>[DependsOn(typeof(AbpLanguagesModule))]</c>, your context replacing
/// <see cref="ILanguagesDbContext"/> with <c>builder.ConfigureLanguages()</c> in its model, your catalogs as
/// <see cref="ITextCatalogSource"/>s, and <see cref="AbpLanguagesOptions"/>.
/// </summary>
[DependsOn(typeof(AbpEntityFrameworkCoreModule), typeof(AbpUsersAbstractionModule))]
public class AbpLanguagesModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddSingleton<ITextBundleWriter, DefaultTextBundleWriter>();
        context.Services.TryAddTransient<ILanguagesEvents, NoLanguagesEvents>();
    }
}

/// <summary>
/// A new site's languages and the version row: the languages in <see cref="AbpLanguagesOptions.Seed"/> when there are
/// none, the first the default. Run by ABP's data seeding, or by the host's own start with <see cref="SeedAsync"/>.
/// </summary>
public class LanguagesSeeder(LanguagesDb languagesDb, IOptions<AbpLanguagesOptions> options) : IDataSeedContributor, ITransientDependency
{
    public Task SeedAsync(DataSeedContext context) => SeedAsync(CancellationToken.None);

    public async Task SeedAsync(CancellationToken ct)
    {
        AbpLanguagesOptions o = options.Value;
        await languagesDb.RunAsync(async db =>
        {
            if (!await db.LanguagesState.AnyAsync(ct))
            {
                db.LanguagesState.Add(new LanguagesState(LanguagesState.TheId) { Version = 1 });
            }

            if (!await db.Languages.AnyAsync(ct))
            {
                int order = 0;
                foreach (LanguageSeed seed in o.Seed)
                {
                    db.Languages.Add(new Language(Guid.NewGuid(), seed.Code, seed.NativeName, seed.EnglishName, LanguagesService.DirectionOf(seed.Direction, seed.Code))
                    {
                        IsDefault = order == 0,
                        Order = ++order,
                        Fallback = seed.Fallback,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
        }, isTransactional: true, ct);
    }
}
