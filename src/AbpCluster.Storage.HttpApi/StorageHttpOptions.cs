using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace AbpCluster.Storage.HttpApi;

/// <summary>How the host serves the storage's admin API (<see cref="AbpClusterStorageHttpApiModule"/>).</summary>
public sealed class StorageHttpOptions
{
    /// <summary>The routes' prefix, the whole path before them: <c>api/storage</c> by default, <c>api/platform/storage</c> for a product that keeps its own.</summary>
    public string RoutePrefix { get; set; } = "api/storage";

    /// <summary>
    /// An authorization policy every action needs, such as the host's site-admin policy. Null (the default): a signed-in
    /// caller, and then the host's <see cref="IStorageRights"/> must say who may manage the storage, since the default
    /// rights refuse everyone unless a policy is set.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// What starting a copy needs besides (<see cref="StorageSensitiveAttribute"/>): the host adds it, such as AbpGuard's
    /// step-up (<c>action =&gt; action.Filters.Add(new StepUpAttribute(StepUpCategories.Backups))</c>). By default nothing.
    /// </summary>
    public Action<ActionModel>? Sensitive { get; set; }

    /// <summary>Anything more on every action (endpoint metadata, a filter of the host's). By default nothing.</summary>
    public Action<ActionModel>? Configure { get; set; }

    /// <summary>The actions' OpenAPI group: null (the default) for the host's own document, a name (<c>storage</c>) for a document of their own.</summary>
    public string? OpenApiGroupName { get; set; }
}

/// <summary>An action that starts work on every stored file (the copy), for <see cref="StorageHttpOptions.Sensitive"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StorageSensitiveAttribute : Attribute;
