# Storage: the settings, and moving files

`AbpCluster.Storage` reads where the app keeps its files from one configuration section, `Storage` unless the app
names another (`ClusterStorageOptions.Section`). Nothing in the app's pages can change it.

## The settings

| Key | For | Meaning |
| --- | --- | --- |
| `Storage:Provider` | all | `FileSystem` (the default), `S3` or `Azure`. |
| `Storage:FileSystem:Path` | FileSystem | The folder. Unset: the app's default (`ClusterStorageOptions.FileSystemPath`, else `data/blobs` beside the app). |
| `Storage:S3:Bucket` | S3 | The bucket every store shares, a prefix each. |
| `Storage:S3:Region` | S3 | AWS's region (`eu-north-1`). Needed for AWS. |
| `Storage:S3:ServiceUrl` | S3 | Another server's address (MinIO, Ceph): `http://minio:9000`. |
| `Storage:S3:ForcePathStyle` | S3 | `bucket` in the path, not the host name. On by default with a `ServiceUrl`. |
| `Storage:S3:AccessKey`, `SecretKey` | S3 | Both or neither. Neither: AWS's own chain signs in (an IAM role, the environment). |
| `Storage:Azure:Container` | Azure | The container every store shares, a prefix each. |
| `Storage:Azure:ConnectionString` | Azure | The account's key, or `UseDevelopmentStorage=true` for Azurite. |
| `Storage:Azure:AccountUrl` | Azure | Instead of a connection string: `https://<account>.blob.core.windows.net`, signing in as the server (workload identity, a managed identity, `az login`). |
| `Storage:CreateBucket` | S3, Azure | Make the bucket or container at first use. Off by default: an operator usually makes it. |

A problem stops the app at start and names the key: `Storage:S3:Bucket is needed for S3 storage.`

## Where the files go

- **File system**: `{path}/host/{store}/{name}`, ABP's own layout (`tenants/{id}` in place of `host` for a tenant's).
  An app that used ABP's file-system provider with the same folder finds its files where they were.
- **S3 and Azure**: `{store}/{name}` in the one bucket or container (`{store}/tenants/{id}/{name}` for a tenant's).

## Moving to another storage

1. Set the new storage under `Storage`, and the one being left under `Storage:Previous` (the same keys:
   `Storage:Previous:Provider`, `Storage:Previous:FileSystem:Path`, and so on). Restart the servers.
2. The site keeps running: new files go to the new storage; a file not moved yet is read from the old one; a delete
   happens in both.
3. Start the copy from the app's admin page (`StorageAdmin.StartCopyAsync`). One server copies for the whole site; every
   server shows how it stands. Each file is checked against the hash the app recorded before it's sent and after it
   arrives; one already there and matching is skipped, so the copy can run again until nothing is left.
4. When the copy says done with nothing failed or missing, remove `Storage:Previous` and restart.

A file the copy reports as **in neither storage** was already lost before the move; one that **doesn't match its hash**
is damaged in the old storage and wasn't copied over a good one.

## After a site takes over, or a restore

The bucket's own replication can be behind the database's, so a standby that takes over can find rows whose files
haven't arrived. `StoredFilesCheck.RunAsync` walks every `IStoredFiles` and counts the files storage lacks, naming the
first five; with `AbpCluster.Sites`, run it from an `ISiteAfterTakeover` and write its outcome where your admins look.
