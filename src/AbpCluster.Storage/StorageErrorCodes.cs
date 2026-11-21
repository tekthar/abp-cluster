namespace AbpCluster.Storage;

/// <summary>The storage's error codes, as an API's answers carry them in <c>error.code</c> (AbpCluster.Storage.HttpApi maps them to statuses).</summary>
public static class StorageErrorCodes
{
    /// <summary>The caller may not manage the storage (403).</summary>
    public const string Forbidden = "AbpCluster.Storage:Forbidden";

    /// <summary>A copy was asked for with no storage being left to copy from (409).</summary>
    public const string NoPrevious = "AbpCluster.Storage:NoPrevious";
}
