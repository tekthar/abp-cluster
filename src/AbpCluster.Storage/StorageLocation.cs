using Microsoft.Extensions.Configuration;

namespace AbpCluster.Storage;

/// <summary>
/// A storage, read from a configuration section: the file system, S3 or a server that speaks it (MinIO, Ceph), or Azure
/// Blob. Only configuration sets it, never a page, so no one signed in to the app can send its files elsewhere.
/// </summary>
public sealed class StorageLocation
{
    public const string FileSystem = "FileSystem";
    public const string S3 = "S3";
    public const string Azure = "Azure";

    public string Provider { get; init; } = FileSystem;
    public string FileSystemPath { get; init; } = "";
    public string? S3ServiceUrl { get; init; }
    public string? S3Region { get; init; }
    public string S3Bucket { get; init; } = "";
    public string? S3AccessKey { get; init; }
    public string? S3SecretKey { get; init; }
    public bool S3ForcePathStyle { get; init; }
    public bool CreateBucket { get; init; }
    public string AzureConnectionString { get; init; } = "";

    /// <summary>The account's address with no connection string: Azure's own sign-in (workload identity, a managed identity), no key.</summary>
    public string? AzureAccountUrl { get; init; }

    public string AzureContainer { get; init; } = "";

    /// <summary>
    /// The storage under a section, checked: a provider it knows, with what that provider needs. Unset, it's the file
    /// system at <paramref name="defaultFileSystemPath"/>. Problems name the section's own keys (<c>Storage:S3:Bucket</c>).
    /// </summary>
    public static StorageLocation Read(IConfiguration configuration, string section, string defaultFileSystemPath)
    {
        IConfigurationSection s = configuration.GetSection(section);
        string provider = s["Provider"] is { Length: > 0 } p ? p.Trim() : FileSystem;
        provider = provider.ToUpperInvariant() switch
        {
            "FILESYSTEM" => FileSystem,
            "S3" => S3,
            "AZURE" => Azure,
            _ => throw new InvalidOperationException($"{section}:Provider is \"{provider}\": it's FileSystem, S3 or Azure."),
        };

        StorageLocation location = new()
        {
            Provider = provider,
            FileSystemPath = Path.GetFullPath(s["FileSystem:Path"] is { Length: > 0 } path ? path : defaultFileSystemPath),
            S3ServiceUrl = s["S3:ServiceUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : null,
            S3Region = s["S3:Region"] is { Length: > 0 } region ? region : null,
            S3Bucket = s["S3:Bucket"] ?? "",
            S3AccessKey = s["S3:AccessKey"] is { Length: > 0 } key ? key : null,
            S3SecretKey = s["S3:SecretKey"] is { Length: > 0 } secret ? secret : null,
            // A server that isn't AWS's (MinIO, Ceph) usually wants path-style addresses.
            S3ForcePathStyle = s.GetValue<bool?>("S3:ForcePathStyle") ?? s["S3:ServiceUrl"] is { Length: > 0 },
            CreateBucket = s.GetValue("CreateBucket", false),
            AzureConnectionString = s["Azure:ConnectionString"] ?? "",
            AzureAccountUrl = s["Azure:AccountUrl"] is { Length: > 0 } account ? account.Trim().TrimEnd('/') : null,
            AzureContainer = s["Azure:Container"] ?? "",
        };

        switch (provider)
        {
            case S3 when location.S3Bucket.Length == 0:
                throw new InvalidOperationException($"{section}:S3:Bucket is needed for S3 storage.");
            case S3 when location.S3ServiceUrl is null && location.S3Region is null:
                throw new InvalidOperationException($"{section}:S3:Region is needed for AWS (or S3:ServiceUrl for another server).");
            case S3 when (location.S3AccessKey is null) != (location.S3SecretKey is null):
                throw new InvalidOperationException($"{section}:S3:AccessKey and S3:SecretKey go together (or neither, for AWS's own sign-in).");
            case Azure when location.AzureContainer.Length == 0 || (location.AzureConnectionString.Length == 0 && location.AzureAccountUrl is null):
                throw new InvalidOperationException($"{section}:Azure:Container is needed for Azure storage, with Azure:ConnectionString or Azure:AccountUrl (to sign in as the server's managed identity).");
            case Azure when location.AzureConnectionString.Length > 0 && location.AzureAccountUrl is not null:
                throw new InvalidOperationException($"{section}:Azure:ConnectionString and Azure:AccountUrl don't go together: it's the account's key, or Azure's own sign-in at the address.");
            case Azure when location.AzureAccountUrl is { } accountUrl && !(Uri.TryCreate(accountUrl, UriKind.Absolute, out Uri? address) && address.Scheme == Uri.UriSchemeHttps):
                throw new InvalidOperationException($"{section}:Azure:AccountUrl is the account's https address, like https://<account>.blob.core.windows.net (Azure signs in over https only).");
        }

        return location;
    }

    /// <summary>The storage under a section when the section is there, else null.</summary>
    public static StorageLocation? ReadIfSet(IConfiguration configuration, string section, string defaultFileSystemPath) =>
        configuration.GetSection(section).Exists() ? Read(configuration, section, defaultFileSystemPath) : null;

    /// <summary>Where it is, for a page or a log, without a secret: a folder, a bucket at a server, an Azure account's container.</summary>
    public string Describe() => Provider switch
    {
        S3 => $"s3://{S3Bucket}" + (S3ServiceUrl is { } url ? $" at {new Uri(url).Authority}" : $" in {S3Region}"),
        Azure => $"{AzureAccount() ?? "Azure"}/{AzureContainer}",
        _ => FileSystemPath,
    };

    private string? AzureAccount() =>
        // https://{account}.blob.core.windows.net, or https://{host}/{account} for an emulator.
        AzureAccountUrl is { } url ? (new Uri(url).Segments is [_, var account, ..] ? account.TrimEnd('/') : new Uri(url).Host.Split('.')[0])
        : AzureConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)).Where(kv => kv.Length == 2 && kv[0].Equals("AccountName", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1]).FirstOrDefault()
        ?? (AzureConnectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase) ? "devstoreaccount1" : null);
}
