using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.Aws;
using Volo.Abp.BlobStoring.Azure;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.ExceptionHandling;
using Volo.Abp.Modularity;

namespace AbpCluster.Storage;

/// <summary>
/// Where the app keeps its files, from configuration (<see cref="ClusterStorageOptions.Section"/>). Add it to the app's
/// <c>[DependsOn]</c> and name the app's stores in <c>PreConfigureServices</c>; its <see cref="StorageState"/> is a
/// singleton by the app's own <c>ConfigureServices</c>, for containers of its own (<see cref="StorageProviders.Use"/>).
/// </summary>
[DependsOn(
    typeof(AbpBlobStoringFileSystemModule),
    typeof(AbpBlobStoringAwsModule),
    typeof(AbpBlobStoringAzureModule),
    typeof(AbpExceptionHandlingModule),
    typeof(AbpClusterModule))]
public sealed class AbpClusterStorageModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context) =>
        AddClusterStorage(context.Services, context.Services.GetConfiguration(), context.Services.ExecutePreConfiguredActions<ClusterStorageOptions>());

    internal static StorageState AddClusterStorage(IServiceCollection services, IConfiguration configuration, ClusterStorageOptions options)
    {
        string defaultPath = options.DefaultFileSystemPath;
        StorageState state = new(
            StorageLocation.Read(configuration, options.Section, defaultPath),
            StorageLocation.ReadIfSet(configuration, options.PreviousSection, defaultPath),
            options);
        services.AddSingleton(options);
        services.AddSingleton(state);

        services.Configure<AbpBlobStoringOptions>(blobs =>
        {
            if (options.ConfigureDefaultContainer)
            {
                blobs.Containers.ConfigureDefault(container => StorageProviders.Use(container, state.Current));
            }

            foreach (string store in options.Stores)
            {
                blobs.Containers.Configure(store, container => StorageProviders.Use(container, state.Current));
                if (state.Previous is { } previous)
                {
                    blobs.Containers.Configure(store + StorageState.PreviousSuffix, container => StorageProviders.Use(container, previous));
                }
            }
        });

        // A store's files under its own name, in whichever storage holds them; reads falling back while files are moved.
        services.Replace(ServiceDescriptor.Transient(typeof(IBlobContainer<>), typeof(StorageContainer<>)));
        services.Replace(ServiceDescriptor.Transient<IBlobFilePathCalculator, StorageFilePathCalculator>());
        services.Replace(ServiceDescriptor.Transient<IAwsBlobNameCalculator, StorageS3NameCalculator>());
        services.Replace(ServiceDescriptor.Transient<IAzureBlobNameCalculator, StorageAzureNameCalculator>());
        services.Replace(ServiceDescriptor.Transient<IAmazonS3ClientFactory, StorageS3ClientFactory>());
        services.TryAddSingleton<StorageAzureClients>();
        services.TryAddTransient<StorageAzureBlobProvider>();

        services.TryAddSingleton<StorageCopier>();
        services.TryAddSingleton<StoredFilesCheck>();
        services.TryAddTransient<StorageAdmin>();
        services.TryAddTransient<IStorageEvents, LoggedStorageEvents>();
        return state;
    }
}
