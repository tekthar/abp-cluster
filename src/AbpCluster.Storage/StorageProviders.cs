using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Volo.Abp;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.Aws;
using Volo.Abp.BlobStoring.Azure;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.MultiTenancy;

namespace AbpCluster.Storage;

/// <summary>
/// Points an ABP blob container at a storage. Each store is a folder in the file system (<c>{path}/host/{store}/{name}</c>,
/// ABP's own layout) or a prefix in the bucket or Azure container (<c>{store}/{name}</c>): one bucket holds every store.
/// </summary>
public static class StorageProviders
{
    /// <summary>The container configuration's key for an Azure account's address (signing in as the server).</summary>
    public const string AzureAccountUrl = "AbpCluster.Storage.Azure.AccountUrl";

    // Path-style addressing per bucket and server, known when containers are configured: ABP's S3 configuration has no
    // place for it, and the client factory sees only that configuration.
    private static readonly ConcurrentDictionary<(string Bucket, string ServiceUrl), bool> PathStyles = new();

    /// <summary>Sends this container's files to the storage given. An app's own containers (a backup's place, say) can use it too.</summary>
    public static void Use(BlobContainerConfiguration container, StorageLocation location)
    {
        switch (location.Provider)
        {
            case StorageLocation.S3:
                PathStyles[(location.S3Bucket, location.S3ServiceUrl ?? "")] = location.S3ForcePathStyle;
                container.UseAws(aws =>
                {
                    aws.ContainerName = location.S3Bucket;
                    aws.Region = location.S3Region ?? "us-east-1";
                    // ABP refuses a null for any of its settings: AWS's own address and sign-in are what's left unset.
                    if (location.S3ServiceUrl is { } serviceUrl)
                    {
                        aws.ServiceURL = serviceUrl;
                    }

                    aws.UseCredentials = location.S3AccessKey is not null;
                    if (location is { S3AccessKey: { } accessKey, S3SecretKey: { } secretKey })
                    {
                        aws.AccessKeyId = accessKey;
                        aws.SecretAccessKey = secretKey;
                    }

                    aws.CreateContainerIfNotExists = location.CreateBucket;
                });
                break;
            case StorageLocation.Azure:
                container.UseAzure(azure =>
                {
                    if (location.AzureAccountUrl is null)
                    {
                        azure.ConnectionString = location.AzureConnectionString;
                    }

                    azure.ContainerName = location.AzureContainer;
                    azure.CreateContainerIfNotExists = location.CreateBucket;
                });
                if (location.AzureAccountUrl is { } accountUrl)
                {
                    // ABP's provider takes a connection string only; with the account's address, this one signs in as the server.
                    container.ProviderType = typeof(StorageAzureBlobProvider);
                    container.SetConfiguration(AzureAccountUrl, accountUrl);
                }

                break;
            default:
                container.UseFileSystem(fs => fs.BasePath = location.FileSystemPath);
                break;
        }
    }

    internal static bool? PathStyle(string bucket, string? serviceUrl) =>
        PathStyles.TryGetValue((bucket, serviceUrl ?? ""), out bool pathStyle) ? pathStyle : null;
}

/// <summary>A store's files in the file system: <c>{path}/host/{store}/{name}</c>, or <c>tenants/{id}</c> for a tenant's, as ABP lays them out.</summary>
public sealed class StorageFilePathCalculator(StorageState state, ICurrentTenant tenant) : IBlobFilePathCalculator
{
    public string Calculate(BlobProviderArgs args) =>
        Path.Combine(args.Configuration.GetFileSystemConfiguration().BasePath,
            tenant.Id is { } id ? Path.Combine("tenants", id.ToString("D")) : "host",
            state.StoreOf(args.ContainerName), args.BlobName);
}

/// <summary>A store's files in a bucket: one bucket, a prefix per store (and a tenant's under it).</summary>
public sealed class StorageS3NameCalculator(StorageState state, ICurrentTenant tenant) : IAwsBlobNameCalculator
{
    public string Calculate(BlobProviderArgs args) => StorageNames.Of(state, tenant, args);
}

/// <summary>A store's files in an Azure container: one container, a prefix per store (and a tenant's under it).</summary>
public sealed class StorageAzureNameCalculator(StorageState state, ICurrentTenant tenant) : IAzureBlobNameCalculator
{
    public string Calculate(BlobProviderArgs args) => StorageNames.Of(state, tenant, args);
}

internal static class StorageNames
{
    public static string Of(StorageState state, ICurrentTenant tenant, BlobProviderArgs args) =>
        tenant.Id is { } id ? $"{state.StoreOf(args.ContainerName)}/tenants/{id:D}/{args.BlobName}" : $"{state.StoreOf(args.ContainerName)}/{args.BlobName}";
}

/// <summary>
/// Azure Blob without a connection string: the account's address, signed in as whatever Azure's own chain finds
/// (workload identity on AKS, a managed identity on a VM, the environment, <c>az login</c> on a laptop), the way S3
/// falls back to AWS's chain. ABP's provider does the rest, as with a connection string.
/// </summary>
public sealed class StorageAzureBlobProvider(IAzureBlobNameCalculator names, IBlobNormalizeNamingService naming, StorageAzureClients clients)
    : AzureBlobProvider(names, naming)
{
    protected override BlobContainerClient GetBlobContainerClient(BlobProviderArgs args) =>
        clients.For(args.Configuration.GetConfiguration<string>(StorageProviders.AzureAccountUrl)).GetBlobContainerClient(GetContainerName(args));
}

/// <summary>
/// One client per Azure account for the server's life: the token Azure gives it is kept and renewed before it ends, not
/// asked for again on every file (ABP makes a client per call, which costs nothing with a connection string).
/// </summary>
public sealed class StorageAzureClients
{
    private readonly Lazy<TokenCredential> _credential = new(() => new DefaultAzureCredential());
    private readonly ConcurrentDictionary<string, BlobServiceClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public BlobServiceClient For(string accountUrl) => _clients.GetOrAdd(accountUrl, url => new BlobServiceClient(new Uri(url), _credential.Value));
}

/// <summary>
/// S3 clients for AWS and for servers that speak S3: a server's own address with path-style requests (MinIO, Ceph),
/// checksums only where the call needs one (not every S3 server knows the newer ones), and the keys configured, or
/// AWS's own chain (an IAM role, the environment) when there are none.
/// </summary>
public sealed class StorageS3ClientFactory : IAmazonS3ClientFactory
{
    public Task<AmazonS3Client> GetAmazonS3Client(AwsBlobProviderConfiguration configuration)
    {
        AmazonS3Config config = new()
        {
            ForcePathStyle = StorageProviders.PathStyle(configuration.ContainerName ?? "", configuration.ServiceURL) ?? configuration.ServiceURL is { Length: > 0 },
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (configuration.ServiceURL is { Length: > 0 } url)
        {
            config.ServiceURL = url;
            config.AuthenticationRegion = configuration.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(configuration.Region);
        }

        return Task.FromResult(configuration.UseCredentials
            ? new AmazonS3Client(new BasicAWSCredentials(configuration.AccessKeyId, configuration.SecretAccessKey), config)
            : new AmazonS3Client(config));
    }
}

/// <summary>
/// A store as the app's code uses it (<c>IBlobContainer&lt;T&gt;</c>): files saved to the storage in use; a file not there
/// yet read from the storage being left, while files are copied; a delete made in both, so a file doesn't come back from
/// the old one.
/// </summary>
public sealed class StorageContainer<TContainer>(IBlobContainerFactory factory, StorageState state) : IBlobContainer<TContainer> where TContainer : class
{
    private readonly string _name = BlobContainerNameAttribute.GetContainerName<TContainer>();

    private IBlobContainer Current => factory.Create(_name);

    private IBlobContainer? Previous => state.FallsBack(_name) ? factory.Create(_name + StorageState.PreviousSuffix) : null;

    public async Task SaveAsync(string name, Stream stream, bool overrideExisting = false, CancellationToken cancellationToken = default)
    {
        if (state.Current.Provider == StorageLocation.FileSystem || stream.CanSeek)
        {
            await Current.SaveAsync(name, stream, overrideExisting, cancellationToken);
            return;
        }

        // A bucket wants the length before the bytes; an upload read as it arrives (hashed and size-checked on the way)
        // can't say it, so it's read to a temporary file first, which goes when it's closed.
        await using FileStream buffer = StorageTemp.Create(state.Options, "upload");
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        await Current.SaveAsync(name, buffer, overrideExisting, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        bool deleted = await Current.DeleteAsync(name, cancellationToken);
        return (Previous is { } previous && await previous.DeleteAsync(name, cancellationToken)) || deleted;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        await Current.ExistsAsync(name, cancellationToken) || (Previous is { } previous && await previous.ExistsAsync(name, cancellationToken));

    public async Task<Stream> GetAsync(string name, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync(name, cancellationToken) ?? throw new AbpException($"There's no file \"{name}\" in {_name}.");

    public async Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default) =>
        await Current.GetOrNullAsync(name, cancellationToken) ?? (Previous is { } previous ? await previous.GetOrNullAsync(name, cancellationToken) : null);
}

internal static class StorageTemp
{
    /// <summary>A temporary file that goes when it's closed.</summary>
    public static FileStream Create(ClusterStorageOptions options, string what) =>
        new(Path.Combine(Path.GetTempPath(), $"{options.TempFilePrefix}{what}-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
}
