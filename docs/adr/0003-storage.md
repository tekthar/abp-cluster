# 0003. Storage: where the files are, and moving them

- **Status:** Accepted
- **Date:** 2026-11-13

## Context

Every ABP app that keeps files meets the same questions once it runs on more than one server or more than one site, and
none of them depends on what the files are:

- **Where they live is the operator's choice, not the code's.** A laptop keeps them on disk, one cloud wants S3 (or
  MinIO, Ceph), another Azure Blob. A server on a cloud should sign in as itself (an IAM role, a managed identity), not
  with a key in its configuration. ABP's blob storing has the providers, but choosing one is code.
- **Moving to another storage can't stop the site.** Files are copied in the background, and until each one has arrived
  it's read from the storage being left. A file damaged on the way must not replace a good one.
- **The app knows its files; the storage doesn't.** A copy, a backup, or a check after a site takes over (the bucket
  replicates on its own, so rows can arrive before their files) all need the list of files the app's tables name.
- **An uploaded file is what its bytes say**, not what its name or the browser says, and a stored file served back must
  never run as a page.

One product built all of this for its attachments, pictures and plugin packages; a second keeps uploaded packages and
promises S3 or Azure "by configuration in production"; a third will keep files later. Which files they keep, who may read
them and how long they're kept stay each app's own.

## Decision

**`AbpCluster.Storage`, a third package beside the cluster's lock and its two sites**, on ABP's blob storing (it adds to
it; it doesn't replace it):

- **`StorageLocation`** reads a storage from a configuration section (`Storage` by default): `FileSystem`, `S3` (AWS by
  region, or another server by address, path-style unless told otherwise) or `Azure` (a connection string, or the
  account's https address to sign in as the server). Without keys, S3 signs in through AWS's own chain. Problems name the
  app's own keys (`Storage:S3:Bucket is needed`). `Describe()` says where it is without a secret.
- **The app's stores** (`ClusterStorageOptions.Stores`, set in `PreConfigureServices`) are ABP containers by name; each is
  a folder in the file system (`{path}/host/{store}/{name}`, ABP's own layout, so files stay where they were) or a
  prefix in one bucket or Azure container (`{store}/{name}`). Other containers go to the same storage unless the app
  says not to; `StoreOf` lets several containers share a folder; `StorageProviders.Use` points an app's own container
  (a backup's place) at another storage.
- **Moving**: `{Section}:Previous` names the storage being left. While it's set, `IBlobContainer<T>` for a store saves
  to the storage in use, reads a file not there yet from the one being left, and deletes in both. **`StorageCopier`**
  copies every stored file across, one run for the whole site (a cluster lock), checking each against the hash the app
  recorded before it's sent and after it arrives, skipping what's already there; how it stands is one row in
  `{schema}.storage_copy`, so every server shows the same copy and one whose server stopped shows as failed.
- **`IStoredFiles`**: each part of the app lists the files its tables name (store, name, hash). The copy, a backup and
  **`StoredFilesCheck`** (how many files storage lacks, the first five named; one run at a time) walk them.
- **`StorageAdmin`** for the app's own admin page and start-up check: where the storage is, a test of each store
  (write, read back, delete), the copy's state and its start. The app keeps its endpoints and who may call them.
- **`IStorageEvents`**: a copy that ended, for the app's audit (the log by default).
- **`FileKinds`**: PNG, JPEG, WebP, GIF, PDF and ZIP known by their first bytes, and `ServeSafely` for the headers that
  keep a served file from being sniffed or run (`nosniff`, a sandbox with nothing allowed, a download's name).

## Consequences

- An app names its stores and lists its files; operators choose the storage in configuration and can move it while the
  site runs. A second app gets S3, Azure, keyless sign-in, the move and the check without writing them.
- Taking over from the product it came from fixed a trap it had: on AWS with no keys (an IAM role, the way its own AWS
  guide sets it up), ABP's S3 configuration was handed a null key and refused it the first time a store was used. The
  package sets only what's given.
- The copy keeps its state in PostgreSQL, like the rest of AbpCluster; without a connection string it runs on the one
  server, with its state in memory.
- Not here: what a backup holds and where it goes, attachments and their versions, who may read a file, retention, or
  signed links. Those are each app's.
