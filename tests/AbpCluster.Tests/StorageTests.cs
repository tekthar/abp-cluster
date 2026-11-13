using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AbpCluster.Storage;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Volo.Abp;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.Aws;
using Volo.Abp.Modularity;

namespace AbpCluster.Tests;

/// <summary>Storage: what configuration it takes and how it's described, with nothing running.</summary>
public sealed class StorageLocationTests
{
    internal static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build();

    [Fact]
    public void With_nothing_set_it_is_the_file_system_at_the_apps_default_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "storage-default");
        StorageLocation location = StorageLocation.Read(Config(), "Storage", folder);
        Assert.Equal((StorageLocation.FileSystem, Path.GetFullPath(folder)), (location.Provider, location.FileSystemPath));
        Assert.Null(StorageLocation.ReadIfSet(Config(), "Storage:Previous", folder));

        string set = Path.Combine(Path.GetTempPath(), "storage-set");
        Assert.Equal(Path.GetFullPath(set), StorageLocation.Read(Config(("Storage:FileSystem:Path", set)), "Storage", folder).FileSystemPath);
    }

    [Fact]
    public void S3_is_described_without_its_keys_and_a_server_that_isnt_AWS_gets_path_style()
    {
        StorageLocation minio = StorageLocation.Read(Config(("Storage:Provider", "s3"), ("Storage:S3:ServiceUrl", "http://minio:9000/"),
            ("Storage:S3:Bucket", "files"), ("Storage:S3:AccessKey", "key"), ("Storage:S3:SecretKey", "very-secret")), "Storage", "/data");
        Assert.Equal((StorageLocation.S3, "http://minio:9000", true), (minio.Provider, minio.S3ServiceUrl, minio.S3ForcePathStyle));
        Assert.Equal("s3://files at minio:9000", minio.Describe());
        Assert.DoesNotContain("secret", minio.Describe());

        StorageLocation aws = StorageLocation.Read(Config(("Storage:Provider", "S3"), ("Storage:S3:Region", "eu-west-1"), ("Storage:S3:Bucket", "files")),
            "Storage", "/data");
        Assert.Equal(("s3://files in eu-west-1", false), (aws.Describe(), aws.S3ForcePathStyle));
    }

    [Fact]
    public void Azure_is_described_by_its_account_and_container()
    {
        StorageLocation keyed = StorageLocation.Read(Config(("Storage:Provider", "Azure"), ("Storage:Azure:Container", "files"),
            ("Storage:Azure:ConnectionString", "DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=c2VjcmV0")), "Storage", "/data");
        Assert.Equal("acme/files", keyed.Describe());
        Assert.DoesNotContain("c2VjcmV0", keyed.Describe());

        StorageLocation signedIn = StorageLocation.Read(Config(("Storage:Provider", "Azure"), ("Storage:Azure:Container", "files"),
            ("Storage:Azure:AccountUrl", "https://acme.blob.core.windows.net/")), "Storage", "/data");
        Assert.Equal(("acme/files", "https://acme.blob.core.windows.net"), (signedIn.Describe(), signedIn.AzureAccountUrl));
    }

    [Theory]
    [InlineData("Weird", "", "", "Files:Provider is \"Weird\"")]
    [InlineData("S3", "", "eu-west-1", "Files:S3:Bucket is needed")]
    [InlineData("S3", "files", "", "Files:S3:Region is needed")]
    public void What_a_provider_needs_is_checked_and_named_by_the_apps_own_section(string provider, string bucket, string region, string problem)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => StorageLocation.Read(
            Config(("Files:Provider", provider), ("Files:S3:Bucket", bucket), ("Files:S3:Region", region)), "Files", "/data"));
        Assert.Contains(problem, ex.Message);
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=a2V5", "https://acme.blob.core.windows.net", "files", "don't go together")]
    [InlineData("", "http://acme.blob.core.windows.net", "files", "https address")]
    [InlineData("", "", "files", "Azure:ConnectionString or Azure:AccountUrl")]
    public void Azure_takes_a_connection_string_or_an_https_address_never_both(string connectionString, string accountUrl, string container, string problem)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => StorageLocation.Read(Config(("Storage:Provider", "Azure"),
            ("Storage:Azure:ConnectionString", connectionString), ("Storage:Azure:AccountUrl", accountUrl), ("Storage:Azure:Container", container)), "Storage", "/data"));
        Assert.Contains(problem, ex.Message);
    }

    [Fact]
    public void A_connection_string_keeps_ABPs_Azure_provider_and_an_address_gets_one_that_signs_in_as_the_server()
    {
        BlobContainerConfiguration keyed = new();
        StorageProviders.Use(keyed, StorageLocation.Read(Config(("Storage:Provider", "Azure"), ("Storage:Azure:Container", "files"),
            ("Storage:Azure:ConnectionString", "UseDevelopmentStorage=true")), "Storage", "/data"));
        Assert.NotEqual(typeof(StorageAzureBlobProvider), keyed.ProviderType);

        BlobContainerConfiguration signedIn = new();
        StorageProviders.Use(signedIn, StorageLocation.Read(Config(("Storage:Provider", "Azure"), ("Storage:Azure:Container", "files"),
            ("Storage:Azure:AccountUrl", "https://acme.blob.core.windows.net")), "Storage", "/data"));
        Assert.Equal(typeof(StorageAzureBlobProvider), signedIn.ProviderType);
        Assert.Equal("https://acme.blob.core.windows.net", signedIn.GetConfiguration<string>(StorageProviders.AzureAccountUrl));
    }

    [Fact]
    public void An_Azure_account_keeps_one_client_so_its_token_is_reused()
    {
        StorageAzureClients clients = new();
        Assert.Same(clients.For("https://acme.blob.core.windows.net"), clients.For("https://ACME.blob.core.windows.net"));
        Assert.NotSame(clients.For("https://acme.blob.core.windows.net"), clients.For("https://other.blob.core.windows.net"));
    }

    [Fact]
    public async Task An_S3_server_named_in_configuration_keeps_its_path_style_and_AWS_keeps_virtual_hosts()
    {
        BlobContainerConfiguration minio = new();
        StorageProviders.Use(minio, StorageLocation.Read(Config(("Storage:Provider", "S3"), ("Storage:S3:ServiceUrl", "http://minio.test:9000"),
            ("Storage:S3:Bucket", "path-style-test"), ("Storage:S3:ForcePathStyle", "false"), ("Storage:S3:AccessKey", "k"), ("Storage:S3:SecretKey", "s")), "Storage", "/data"));
        using AmazonS3Client client = await new StorageS3ClientFactory().GetAmazonS3Client(new AwsBlobProviderConfiguration(minio));
        Assert.False(((AmazonS3Config)client.Config).ForcePathStyle);

        BlobContainerConfiguration aws = new();
        StorageProviders.Use(aws, StorageLocation.Read(Config(("Storage:Provider", "S3"), ("Storage:S3:Region", "eu-north-1"),
            ("Storage:S3:Bucket", "virtual-host-test")), "Storage", "/data"));
        using AmazonS3Client awsClient = await new StorageS3ClientFactory().GetAmazonS3Client(new AwsBlobProviderConfiguration(aws));
        Assert.False(((AmazonS3Config)awsClient.Config).ForcePathStyle);
        Assert.Equal("eu-north-1", awsClient.Config.RegionEndpoint.SystemName);
        // No keys: AWS's own sign-in (an IAM role), with no address of a server.
        AwsBlobProviderConfiguration iam = new(aws);
        Assert.Equal((false, (string?)null), (iam.UseCredentials, iam.ServiceURL));
    }

    [Fact]
    public void A_store_is_the_same_whichever_storage_holds_it_unless_the_app_says_otherwise()
    {
        StorageLocation here = StorageLocation.Read(Config(), "Storage", "/data");
        StorageState plain = new(here, null, new ClusterStorageOptions());
        Assert.Equal(("files", "files"), (plain.StoreOf("files"), plain.StoreOf("files" + StorageState.PreviousSuffix)));

        ClusterStorageOptions grouped = new() { StoreOf = c => c.StartsWith("backup-", StringComparison.Ordinal) ? "backups" : StorageState.DefaultStoreOf(c) };
        StorageState state = new(here, null, grouped);
        Assert.Equal(("backups", "files"), (state.StoreOf("backup-offsite"), state.StoreOf("files.previous")));
    }

    [Fact]
    public void Files_are_known_by_their_bytes_not_their_names()
    {
        Assert.Equal(FileKinds.Png, FileKinds.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Equal(FileKinds.Jpeg, FileKinds.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(FileKinds.WebP, FileKinds.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(FileKinds.Gif, FileKinds.Sniff("GIF89a..."u8));
        Assert.Equal(FileKinds.Pdf, FileKinds.Sniff("%PDF-1.7"u8));
        Assert.Equal(FileKinds.Zip, FileKinds.Sniff([0x50, 0x4B, 0x03, 0x04, 0x14]));
        Assert.Null(FileKinds.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(FileKinds.Sniff([0xC2, 0x89, 0x50, 0x4E, 0x47]));
        Assert.Null(FileKinds.Sniff("%PDF-1.7"u8, FileKinds.Images));
        Assert.Equal(FileKinds.Png, FileKinds.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], FileKinds.Images));
    }

    [Fact]
    public void A_stored_file_is_served_as_its_type_and_never_run_as_a_page()
    {
        DefaultHttpContext context = new();
        FileKinds.ServeSafely(context.Response, "app-1.0.0.zip");
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Contains("sandbox", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("attachment; filename=app-1.0.0.zip", context.Response.Headers.ContentDisposition.ToString());
    }
}

/// <summary>Storage in an ABP application, on the file system: the layout, reading from the storage being left, the copy, the test and the check.</summary>
[Collection("storage")]
public sealed class StorageAppTests : IDisposable
{
    [BlobContainerName("things")]
    public sealed class Things;

    [BlobContainerName("other")]
    public sealed class Other;

    [DependsOn(typeof(AbpClusterStorageModule))]
    public sealed class Host : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context) =>
            PreConfigure<ClusterStorageOptions>(o =>
            {
                o.Stores.Add("things");
                o.Schema = context.Services.GetConfiguration()["Test:Schema"] ?? "cluster";
            });

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddTransient<IStoredFiles, ListedFiles>();
            context.Services.AddSingleton<Events>();
            context.Services.AddTransient<IStorageEvents>(sp => sp.GetRequiredService<Events>());
        }
    }

    /// <summary>The files the "app" names, as its tables would: set by each test.</summary>
    public sealed class ListedFiles : IStoredFiles
    {
        public static List<StoredFile> Files { get; } = [];

        public async IAsyncEnumerable<StoredFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (StoredFile file in Files.ToList())
            {
                await Task.Yield();
                yield return file;
            }
        }
    }

    public sealed class Events : IStorageEvents
    {
        public List<(StorageCopyDto Done, string From, string To)> Copies { get; } = [];

        public Task CopiedAsync(StorageCopyDto done, StorageLocation from, StorageLocation to, CancellationToken cancellationToken)
        {
            Copies.Add((done, from.Describe(), to.Describe()));
            return Task.CompletedTask;
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "abp-storage-tests", Guid.NewGuid().ToString("N"));

    private string Current => Path.Combine(_root, "current");

    private string Previous => Path.Combine(_root, "previous");

    public void Dispose()
    {
        ListedFiles.Files.Clear();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static async Task<IAbpApplicationWithInternalServiceProvider> StartAsync(params (string Key, string Value)[] settings)
    {
        IConfiguration configuration = StorageLocationTests.Config(settings);
        IAbpApplicationWithInternalServiceProvider app = await AbpApplicationFactory.CreateAsync<Host>(o => o.Services.ReplaceConfiguration(configuration));
        await app.InitializeAsync();
        return app;
    }

    private Task<IAbpApplicationWithInternalServiceProvider> StartMovingAsync() =>
        StartAsync(("Storage:FileSystem:Path", Current), ("Storage:Previous:Provider", "FileSystem"), ("Storage:Previous:FileSystem:Path", Previous));

    private static void Put(string folder, string store, string name, string text)
    {
        string path = Path.Combine(folder, "host", store, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task A_stores_files_are_in_its_own_folder_as_ABP_lays_them_out()
    {
        using IAbpApplicationWithInternalServiceProvider app = await StartAsync(("Storage:FileSystem:Path", Current));
        await app.ServiceProvider.GetRequiredService<IBlobContainer<Things>>().SaveAsync("a/b.txt", new MemoryStream("one"u8.ToArray()));
        await app.ServiceProvider.GetRequiredService<IBlobContainer<Other>>().SaveAsync("c.txt", new MemoryStream("two"u8.ToArray()));

        Assert.Equal("one", File.ReadAllText(Path.Combine(Current, "host", "things", "a", "b.txt")));
        // Not a store, but the default container goes to the same storage.
        Assert.Equal("two", File.ReadAllText(Path.Combine(Current, "host", "other", "c.txt")));
        Assert.Null(app.ServiceProvider.GetRequiredService<StorageState>().Previous);
    }

    [Fact]
    public async Task While_files_are_moved_one_not_moved_yet_is_read_from_the_storage_being_left()
    {
        Put(Previous, "things", "old.txt", "from before");
        using IAbpApplicationWithInternalServiceProvider app = await StartMovingAsync();
        IBlobContainer<Things> things = app.ServiceProvider.GetRequiredService<IBlobContainer<Things>>();

        Assert.True(await things.ExistsAsync("old.txt"));
        Assert.Equal("from before", Encoding.UTF8.GetString(await things.GetAllBytesAsync("old.txt")));

        await things.SaveAsync("new.txt", new MemoryStream("new"u8.ToArray()));
        Assert.True(File.Exists(Path.Combine(Current, "host", "things", "new.txt")));
        Assert.False(File.Exists(Path.Combine(Previous, "host", "things", "new.txt")));

        // A delete is made in both, so the file doesn't come back from the storage being left.
        Assert.True(await things.DeleteAsync("old.txt"));
        Assert.False(await things.ExistsAsync("old.txt"));
        Assert.False(File.Exists(Path.Combine(Previous, "host", "things", "old.txt")));
    }

    [Fact]
    public async Task A_container_that_isnt_a_store_never_reads_from_the_storage_being_left()
    {
        Put(Previous, "other", "old.txt", "from before");
        using IAbpApplicationWithInternalServiceProvider app = await StartMovingAsync();
        Assert.Null(await app.ServiceProvider.GetRequiredService<IBlobContainer<Other>>().GetOrNullAsync("old.txt"));
    }

    [Fact]
    public async Task The_copy_takes_each_file_across_once_checked_against_its_hash()
    {
        Put(Previous, "things", "moved.txt", "moved");
        Put(Previous, "things", "here.txt", "here");
        Put(Current, "things", "here.txt", "here");
        Put(Previous, "things", "damaged.txt", "not what was recorded");
        Put(Previous, "things", "unrecorded.txt", "no hash");
        ListedFiles.Files.AddRange([
            new StoredFile("things", "moved.txt", Sha("moved")),
            new StoredFile("things", "here.txt", Sha("here")),
            new StoredFile("things", "gone.txt", Sha("gone")),
            new StoredFile("things", "damaged.txt", Sha("what was recorded")),
            new StoredFile("things", "unrecorded.txt", null),
        ]);
        using IAbpApplicationWithInternalServiceProvider app = await StartMovingAsync();
        StorageAdmin admin = app.ServiceProvider.GetRequiredService<StorageAdmin>();

        Assert.Equal("running", (await admin.StartCopyAsync(default)).State);
        StorageCopyDto done = await app.ServiceProvider.GetRequiredService<StorageCopier>().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(("done", 5, 2, 1, 1, 1), (done.State, done.Total, done.Copied, done.Skipped, done.Missing, done.Failed));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(Current, "host", "things", "moved.txt")));
        Assert.Equal("no hash", File.ReadAllText(Path.Combine(Current, "host", "things", "unrecorded.txt")));
        Assert.False(File.Exists(Path.Combine(Current, "host", "things", "damaged.txt")));
        Assert.Contains(done.Problems, p => p.Name == "damaged.txt" && p.Problem.Contains("doesn't match"));
        Assert.Contains(done.Problems, p => p.Name == "gone.txt" && p.Problem.Contains("neither"));

        (StorageCopyDto recorded, string from, string to) = Assert.Single(app.ServiceProvider.GetRequiredService<Events>().Copies);
        Assert.Equal(("done", Path.GetFullPath(Previous), Path.GetFullPath(Current)), (recorded.State, from, to));

        // Again: what's there is skipped; what's in neither, or damaged, is still said.
        await admin.StartCopyAsync(default);
        StorageCopyDto again = await app.ServiceProvider.GetRequiredService<StorageCopier>().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((3, 0, 1, 1), (again.Skipped, again.Copied, again.Missing, again.Failed));
    }

    [Fact]
    public async Task With_no_storage_being_left_a_copy_is_refused_naming_the_apps_setting()
    {
        using IAbpApplicationWithInternalServiceProvider app = await StartAsync(("Storage:FileSystem:Path", Current));
        UserFriendlyException ex = await Assert.ThrowsAsync<UserFriendlyException>(() => app.ServiceProvider.GetRequiredService<StorageAdmin>().StartCopyAsync(default));
        Assert.Contains("Storage:Previous", ex.Message);
        Assert.Equal("idle", (await app.ServiceProvider.GetRequiredService<StorageAdmin>().CopyStatusAsync(default)).State);
    }

    [Fact]
    public async Task Each_store_is_tested_by_writing_reading_and_deleting_a_file()
    {
        using IAbpApplicationWithInternalServiceProvider app = await StartMovingAsync();
        StorageAdmin admin = app.ServiceProvider.GetRequiredService<StorageAdmin>();

        StorageTestDto test = Assert.Single(await admin.TestAsync(default));
        Assert.Equal(("things", true, (string?)null), (test.Store, test.Ok, test.Error));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Current, "host", "things"), "*", SearchOption.AllDirectories));

        StorageDto described = admin.Describe();
        Assert.Equal((StorageLocation.FileSystem, Path.GetFullPath(Current)), (described.Current.Provider, described.Current.Location));
        Assert.Equal(Path.GetFullPath(Previous), described.Previous?.Location);
        Assert.Equal(["things"], described.Stores);
    }

    [Fact]
    public async Task A_store_that_cant_be_written_says_why()
    {
        Directory.CreateDirectory(_root);
        string notAFolder = Path.Combine(_root, "a-file");
        File.WriteAllText(notAFolder, "");
        using IAbpApplicationWithInternalServiceProvider app = await StartAsync(("Storage:FileSystem:Path", notAFolder));

        StorageTestDto test = Assert.Single(await app.ServiceProvider.GetRequiredService<StorageAdmin>().TestAsync(default));
        Assert.False(test.Ok);
        Assert.False(string.IsNullOrEmpty(test.Error));
    }

    [Fact]
    public async Task The_check_counts_the_files_storage_lacks_and_names_the_first()
    {
        Put(Current, "things", "here.txt", "here");
        ListedFiles.Files.AddRange([new StoredFile("things", "here.txt", null), new StoredFile("things", "lost.txt", null)]);
        using IAbpApplicationWithInternalServiceProvider app = await StartAsync(("Storage:FileSystem:Path", Current));

        StoredFilesCheckDto? check = await app.ServiceProvider.GetRequiredService<StoredFilesCheck>().RunAsync(default);
        Assert.Equal((2, 1), (check!.Total, check.Missing));
        Assert.Equal(["things/lost.txt"], check.Named);
    }
}

/// <summary>
/// The copy on a real PostgreSQL: how it stands is kept for every server. Needs Postgres as <see cref="ClusterLocksTests"/>
/// does; each test has a schema of its own.
/// </summary>
[Collection("storage")]
public sealed class StorageDatabaseTests : IAsyncLifetime
{
    private readonly string _schema = "storage_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "abp-storage-db-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        StorageAppTests.ListedFiles.Files.Clear();
        await using NpgsqlConnection connection = new(ClusterLocksTests.Server);
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<IAbpApplicationWithInternalServiceProvider> StartAsync() => StorageAppTests.StartAsync(
        ("ConnectionStrings:Default", ClusterLocksTests.Server), ("Test:Schema", _schema),
        ("Storage:FileSystem:Path", Path.Combine(_root, "current")),
        ("Storage:Previous:Provider", "FileSystem"), ("Storage:Previous:FileSystem:Path", Path.Combine(_root, "previous")));

    [Fact]
    public async Task Another_server_sees_the_copy_as_it_ended_and_a_copy_whose_server_stopped_as_failed()
    {
        string path = Path.Combine(_root, "previous", "host", "things", "f.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "f");
        StorageAppTests.ListedFiles.Files.Add(new StoredFile("things", "f.txt", null));

        using IAbpApplicationWithInternalServiceProvider copying = await StartAsync();
        using IAbpApplicationWithInternalServiceProvider other = await StartAsync();
        Assert.Equal("idle", (await other.ServiceProvider.GetRequiredService<StorageAdmin>().CopyStatusAsync(default)).State);

        await copying.ServiceProvider.GetRequiredService<StorageAdmin>().StartCopyAsync(default);
        await copying.ServiceProvider.GetRequiredService<StorageCopier>().WaitAsync(TimeSpan.FromSeconds(20));
        StorageCopyDto seen = await other.ServiceProvider.GetRequiredService<StorageAdmin>().CopyStatusAsync(default);
        Assert.Equal(("done", 1), (seen.State, seen.Copied));

        // A row left running, with no server holding the copy's lock: that server stopped.
        await using NpgsqlConnection connection = new(ClusterLocksTests.Server);
        await connection.OpenAsync();
        await using NpgsqlCommand running = new($$"""UPDATE "{{_schema}}".storage_copy SET "Status" = jsonb_set("Status", '{state}', '"running"')""", connection);
        await running.ExecuteNonQueryAsync();
        StorageCopyDto stopped = await other.ServiceProvider.GetRequiredService<StorageAdmin>().CopyStatusAsync(default);
        Assert.Equal("failed", stopped.State);
        Assert.Contains(stopped.Problems, p => p.Problem.Contains("stopped before it finished"));
    }
}
