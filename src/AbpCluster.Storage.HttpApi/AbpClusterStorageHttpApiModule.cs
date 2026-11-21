using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Modularity;

namespace AbpCluster.Storage.HttpApi;

/// <summary>
/// AbpCluster.Storage's admin API over HTTP: <c>[DependsOn(typeof(AbpClusterStorageHttpApiModule))]</c> in the host's
/// module, and <see cref="StorageHttpOptions"/> for the routes' prefix, the policy and what starting a copy needs.
/// </summary>
[DependsOn(typeof(AbpClusterStorageModule), typeof(AbpAspNetCoreMvcModule))]
public class AbpClusterStorageHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<IMvcBuilder>(mvc => mvc.AddApplicationPartIfNotExists(typeof(AbpClusterStorageHttpApiModule).Assembly));

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddTransient<IStorageRights, PolicyStorageRights>();
        context.Services.AddTransient<IConfigureOptions<MvcOptions>, StorageHttpMvcSetup>();
        // The codes as the API means them; ABP would answer each with 403.
        Configure<AbpExceptionHttpStatusCodeOptions>(o =>
        {
            o.Map(StorageErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            o.Map(StorageErrorCodes.NoPrevious, HttpStatusCode.Conflict);
        });
    }
}

/// <summary>Puts <see cref="StorageHttpConvention"/> into MVC once the host's <see cref="StorageHttpOptions"/> are known.</summary>
internal sealed class StorageHttpMvcSetup(IOptions<StorageHttpOptions> options) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions mvc) => mvc.Conventions.Add(new StorageHttpConvention(options.Value));
}

/// <summary>
/// The package's controller under the host's prefix, behind its policy (or a signed-in caller), visible to the API
/// explorer in the host's group, with the host's additions on the sensitive action and on every action.
/// </summary>
internal sealed class StorageHttpConvention(StorageHttpOptions options) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        AttributeRouteModel prefix = new(new RouteAttribute(options.RoutePrefix.Trim('/')));
        AuthorizationPolicy signedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (ControllerModel controller in application.Controllers.Where(c => c.ControllerType.Assembly == typeof(StorageHttpConvention).Assembly))
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(prefix, selector.AttributeRouteModel);
            }

            controller.Filters.Add(options.Policy is { Length: > 0 } policy ? new AuthorizeFilter(policy) : new AuthorizeFilter(signedIn));
            controller.ApiExplorer.IsVisible = true;
            controller.ApiExplorer.GroupName = options.OpenApiGroupName;
            foreach (ActionModel action in controller.Actions)
            {
                if (action.Attributes.OfType<StorageSensitiveAttribute>().Any())
                {
                    options.Sensitive?.Invoke(action);
                }

                options.Configure?.Invoke(action);
            }
        }
    }
}
