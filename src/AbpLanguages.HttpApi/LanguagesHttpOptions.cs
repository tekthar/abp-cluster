using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.Options;

namespace AbpLanguages.HttpApi;

/// <summary>How the host serves the languages API (<see cref="AbpLanguagesHttpApiModule"/>).</summary>
public sealed class LanguagesHttpOptions
{
    /// <summary>The routes' prefix, the whole path before them: <c>api/localization</c> by default, <c>api/platform</c> for a product that keeps its own.</summary>
    public string RoutePrefix { get; set; } = "api/localization";

    /// <summary>
    /// The policy of those who may change languages and texts (the host's site admins). Null: the host's
    /// <see cref="ILanguagesRights"/> decides, and the default rights then let no one change anything. Reading the enabled
    /// languages and a language's texts needs no sign-in either way.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>Anything more on every action (endpoint metadata, a filter of the host's). By default nothing.</summary>
    public Action<ActionModel>? Configure { get; set; }

    /// <summary>The actions' OpenAPI group: null (the default) for the host's own document, a name (<c>languages</c>) for a document of their own.</summary>
    public string? OpenApiGroupName { get; set; }
}

/// <summary>
/// Who may change the site's languages and texts, and see every language with its progress: the site's admins, as the
/// host decides. A host gives its own (registered after the module's), reading the caller its own way.
/// </summary>
public interface ILanguagesRights
{
    Task<bool> MayManageAsync(CancellationToken cancellationToken);
}

/// <summary>The default: a signed-in caller who passes the host's <see cref="LanguagesHttpOptions.Policy"/>; no one when there's no policy.</summary>
internal sealed class PolicyLanguagesRights(IHttpContextAccessor http, IAuthorizationService authorization, IOptions<LanguagesHttpOptions> options) : ILanguagesRights
{
    public async Task<bool> MayManageAsync(CancellationToken cancellationToken) =>
        options.Value.Policy is { Length: > 0 } policy && http.HttpContext?.User is { Identity.IsAuthenticated: true } user
        && (await authorization.AuthorizeAsync(user, policy)).Succeeded;
}
