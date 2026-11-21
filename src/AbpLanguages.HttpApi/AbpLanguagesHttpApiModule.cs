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

namespace AbpLanguages.HttpApi;

/// <summary>
/// AbpLanguages' API over HTTP: <c>[DependsOn(typeof(AbpLanguagesHttpApiModule))]</c> in the host's module, and
/// <see cref="LanguagesHttpOptions"/> for the routes' prefix and who may change languages and texts.
/// </summary>
[DependsOn(typeof(AbpLanguagesModule), typeof(AbpAspNetCoreMvcModule))]
public class AbpLanguagesHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<IMvcBuilder>(mvc => mvc.AddApplicationPartIfNotExists(typeof(AbpLanguagesHttpApiModule).Assembly));

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpContextAccessor();
        context.Services.TryAddTransient<ILanguagesRights, PolicyLanguagesRights>();
        context.Services.AddTransient<IConfigureOptions<MvcOptions>, LanguagesHttpMvcSetup>();
        // The codes as the API means them; ABP would answer each with 403.
        Configure<AbpExceptionHttpStatusCodeOptions>(o =>
        {
            o.Map(LanguagesErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            o.Map(LanguagesErrorCodes.Invalid, HttpStatusCode.BadRequest);
        });
    }
}

/// <summary>Puts <see cref="LanguagesHttpConvention"/> into MVC once the host's <see cref="LanguagesHttpOptions"/> are known.</summary>
internal sealed class LanguagesHttpMvcSetup(IOptions<LanguagesHttpOptions> options) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions mvc) => mvc.Conventions.Add(new LanguagesHttpConvention(options.Value));
}

/// <summary>
/// The package's controller under the host's prefix. Its actions want a signed-in caller unless they're open to anyone
/// (<c>[AllowAnonymous]</c>: the languages and the texts), and who may change things is the rights' check in each.
/// </summary>
internal sealed class LanguagesHttpConvention(LanguagesHttpOptions options) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        AttributeRouteModel prefix = new(new RouteAttribute(options.RoutePrefix.Trim('/')));
        AuthorizationPolicy signedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (ControllerModel controller in application.Controllers.Where(c => c.ControllerType.Assembly == typeof(LanguagesHttpConvention).Assembly))
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(prefix, selector.AttributeRouteModel);
            }

            controller.Filters.Add(new AuthorizeFilter(signedIn));
            controller.ApiExplorer.IsVisible = true;
            controller.ApiExplorer.GroupName = options.OpenApiGroupName;
            foreach (ActionModel action in controller.Actions)
            {
                options.Configure?.Invoke(action);
            }
        }
    }
}
