using System.Text.RegularExpressions;

namespace AbpCluster.Storage;

/// <summary>
/// What the app tells the storage, in its module's <c>PreConfigureServices</c>:
/// <c>PreConfigure&lt;ClusterStorageOptions&gt;(o =&gt; o.Stores.Add("attachments"))</c>.
/// </summary>
public sealed partial class ClusterStorageOptions
{
    /// <summary>The configuration section the storage is read from; the storage being left is <c>{Section}:Previous</c>.</summary>
    public string Section { get; set; } = "Storage";

    /// <summary>
    /// The app's stores: each an ABP blob container by name (<c>[BlobContainerName("attachments")]</c>), a folder in the
    /// file system or a prefix in the bucket. While files are moved, these are the ones read from the storage being left.
    /// </summary>
    public List<string> Stores { get; } = [];

    /// <summary>The file system's folder when the section names none. Unset: <c>data/blobs</c> beside the app.</summary>
    public string? FileSystemPath { get; set; }

    /// <summary>Whether containers that aren't stores (any other name) go to the same storage. On by default.</summary>
    public bool ConfigureDefaultContainer { get; set; } = true;

    /// <summary>
    /// The store a container's files belong to: its folder or prefix. Unset, it's the container's own name, and the same
    /// for the name with <see cref="StorageState.PreviousSuffix"/>. An app gives several containers one folder here.
    /// </summary>
    public Func<string, string>? StoreOf { get; set; }

    /// <summary>The database the copy keeps how it stands in. Unset: the connection string named <see cref="ConnectionStringName"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The connection string's name, when <see cref="ConnectionString"/> isn't given.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>The schema of the copy's table (<c>storage_copy</c>), made where it isn't.</summary>
    public string Schema { get; set; } = "cluster";

    /// <summary>The cluster lock one copy holds while it runs, across the site's servers.</summary>
    public string CopyLockName { get; set; } = "storage-copy";

    /// <summary>The cluster lock one check of the stored files holds while it runs.</summary>
    public string CheckLockName { get; set; } = "storage-files-check";

    /// <summary>The folder a store's test writes its file to, and deletes it from.</summary>
    public string TestFolder { get; set; } = ".storage-test";

    /// <summary>The start of temporary files' names, for an upload going to a bucket or a file being copied.</summary>
    public string TempFilePrefix { get; set; } = "abp-storage-";

    /// <summary>A setting's full key, as problems name it: <c>Storage:Previous</c>.</summary>
    public string Setting(string key) => $"{Section}:{key}";

    internal string PreviousSection => Setting("Previous");

    internal string DefaultFileSystemPath => Path.GetFullPath(FileSystemPath is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, "data", "blobs"));

    internal string QuotedSchema => SchemaName().IsMatch(Schema)
        ? $"\"{Schema}\""
        : throw new InvalidOperationException($"The storage's schema \"{Schema}\" isn't a plain name (lower-case letters, digits and _).");

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}

/// <summary>The storage in use, and the one being left while files are copied (null when there's none).</summary>
public sealed class StorageState(StorageLocation current, StorageLocation? previous, ClusterStorageOptions options)
{
    /// <summary>A store's container in the storage being left: <c>attachments.previous</c>.</summary>
    public const string PreviousSuffix = ".previous";

    public StorageLocation Current { get; } = current;

    public StorageLocation? Previous { get; } = previous;

    public ClusterStorageOptions Options { get; } = options;

    /// <summary>The store a container belongs to, whichever storage holds it: its folder in the file system, its prefix in a bucket.</summary>
    public string StoreOf(string container) =>
        Options.StoreOf?.Invoke(container) ?? DefaultStoreOf(container);

    /// <summary>The container's own name, without <see cref="PreviousSuffix"/>.</summary>
    public static string DefaultStoreOf(string container) =>
        container.EndsWith(PreviousSuffix, StringComparison.Ordinal) ? container[..^PreviousSuffix.Length] : container;

    /// <summary>Whether reads of this container fall back to the storage being left.</summary>
    public bool FallsBack(string container) => Previous is not null && Options.Stores.Contains(container);
}
