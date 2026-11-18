using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace NativeWeave.Reference.TaskService;

internal sealed record UploadManifest(long Version, string OperationId, string Tenant, string Principal, string ItemId,
    string RequestKey, long ExpectedVersion, string? ContentHash, long ContentLength, string State, long CreatedAt);
internal sealed record RecoveryManifest(long Version, string OperationId, string Scope, string RequestKey, string RequestHash, long CreatedAt);

/// <summary>One process owns this finite local filesystem profile; blobs precede database references.</summary>
internal sealed class BlobStore : IDisposable
{
    internal const long MaximumAttachment = 64L * 1024 * 1024;
    private readonly ServiceConfiguration _configuration;
    private readonly FileStream _processLock;
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private long _reserved;
    internal string Root { get; }
    internal string Staging { get; }
    internal string Blobs { get; }
    internal string Recovery { get; }

    internal BlobStore(ServiceConfiguration configuration)
    {
        _configuration = configuration; Root = Path.GetFullPath(configuration.BlobDirectory);
        Directory.CreateDirectory(Root);
        _processLock = new FileStream(Path.Combine(Root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Staging = Path.Combine(Root, "staging"); Blobs = Path.Combine(Root, "blobs"); Recovery = Path.Combine(Root, "recovery");
        Directory.CreateDirectory(Staging); Directory.CreateDirectory(Blobs); Directory.CreateDirectory(Recovery);
        DurableDirectory.Flush(Root);
    }

    internal UploadLease Begin(AuthIdentity identity, string itemId, string key, long version, long length)
    {
        if (length < 0 || length > MaximumAttachment) throw new ApplicationProblem(413, "attachment_limit");
        string operation = Guid.NewGuid().ToString("D");
        long reservation = checked(length + 16_384);
        lock (_gate)
        {
            long stored = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (_active.Count >= _configuration.MaxUploads || stored + _reserved + reservation > _configuration.MaxBlobBytes ||
                Directory.EnumerateFiles(Staging, "*.json").Take(8192).Count() >= 8192)
                throw new ApplicationProblem(503, "upload_capacity");
            _active.Add(operation); _reserved += reservation;
        }
        var manifest = new UploadManifest(1, operation, identity.Tenant, identity.Principal, itemId, key, version, null, length, "uploading", TaskService.Now());
        try { WriteManifest(manifest); return new(this, manifest, reservation); }
        catch { Release(operation, reservation); throw; }
    }

    internal FileStream Open(Attachment attachment)
    {
        string path = BlobPath(attachment.ContentHash);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32_768, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != attachment.ContentLength) { stream.Dispose(); throw new InvalidOperationException("A database reference does not match the immutable blob."); }
        return stream;
    }

    internal IReadOnlyList<UploadManifest> Manifests() => Directory.EnumerateFiles(Staging, "*.json").Select(ReadManifest).ToArray();
    internal HashSet<string> ProtectedOperations() => Manifests().Where(x => x.ContentHash is not null || IsActive(x.OperationId)).Select(x => x.OperationId)
        .Concat(Directory.EnumerateFiles(Recovery, "*.json").Select(path => Canonical.Id(Path.GetFileNameWithoutExtension(path)))).ToHashSet(StringComparer.Ordinal);

    internal void ProtectCommit(OperationResult operation)
    {
        lock (_gate)
        {
            if (Directory.EnumerateFiles(Recovery, "*.json").Take(8192).Count() >= 8192 ||
                Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) + _reserved + 8192 > _configuration.MaxBlobBytes)
                throw new ApplicationProblem(503, "recovery_capacity");
            var manifest = new RecoveryManifest(1, operation.OperationId, operation.Scope, operation.RequestKey, operation.RequestHash, operation.CompletedAt);
            string path = Path.Combine(Recovery, operation.OperationId + ".json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AppJson.Default.RecoveryManifest);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            DurableDirectory.Flush(Recovery);
        }
    }

    internal void ConfirmCommit(string operationId)
    {
        try { File.Delete(Path.Combine(Recovery, Canonical.Id(operationId) + ".json")); DurableDirectory.Flush(Recovery); }
        catch (IOException) { Console.Error.WriteLine("confirmed_commit_protection_cleanup_pending"); }
    }

    internal void ConfirmRecoveredUpload(string operationId)
    {
        try { File.Delete(ManifestPath(operationId)); DurableDirectory.Flush(Staging); }
        catch (IOException) { Console.Error.WriteLine("confirmed_upload_protection_cleanup_pending"); }
    }

    internal async Task<(int Blobs, int Staging)> CollectAsync(Func<string, CancellationToken, Task<bool>> referenced, CancellationToken token)
    {
        long cutoff = TaskService.Now() - checked(_configuration.OrphanGraceSeconds * 1000L);
        var manifests = Manifests();
        var protectedHashes = manifests.Where(manifest => manifest.ContentHash is not null).Select(manifest => manifest.ContentHash!).ToHashSet(StringComparer.Ordinal);
        int deletedStaging = 0; int deletedBlobs = 0;
        foreach (var manifest in manifests)
        {
            token.ThrowIfCancellationRequested();
            if (manifest.ContentHash is not null || manifest.CreatedAt >= cutoff || IsActive(manifest.OperationId)) continue;
            // An upload with no recorded content hash cannot have entered blob rename or database binding.
            string part = PartPath(manifest.OperationId);
            if (File.Exists(part)) File.Delete(part);
            File.Delete(ManifestPath(manifest.OperationId)); deletedStaging++;
        }
        foreach (string path in Directory.EnumerateFiles(Staging).Where(path => path.EndsWith(".part", StringComparison.Ordinal) || path.EndsWith(".json.new", StringComparison.Ordinal)))
        {
            string fileName = Path.GetFileName(path);
            if (fileName.Length < 36) continue;
            string id = fileName[..36];
            if (!Guid.TryParseExact(id, "D", out _) || IsActive(id) || File.Exists(ManifestPath(id)) ||
                new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() >= cutoff) continue;
            File.Delete(path); deletedStaging++;
        }
        foreach (string path in Directory.EnumerateFiles(Blobs))
        {
            token.ThrowIfCancellationRequested();
            string hash = Path.GetFileName(path);
            if (!ValidHash(hash) || protectedHashes.Contains(hash) || new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() >= cutoff) continue;
            if (await referenced(hash, token).ConfigureAwait(false)) continue;
            // One process owns Root. Active uploads always persist their hash before a rename.
            lock (_gate)
            {
                if (Manifests().Any(manifest => manifest.ContentHash == hash)) continue;
                try { File.Delete(path); deletedBlobs++; }
                catch (IOException) { /* An open Windows reader retains the file; a later collection retries. */ }
            }
        }
        DurableDirectory.Flush(Staging); DurableDirectory.Flush(Blobs);
        return (deletedBlobs, deletedStaging);
    }

    private bool IsActive(string id) { lock (_gate) return _active.Contains(id); }
    private string ManifestPath(string id) => Path.Combine(Staging, Canonical.Id(id) + ".json");
    private string PartPath(string id) => Path.Combine(Staging, Canonical.Id(id) + ".part");
    private string BlobPath(string hash) => ValidHash(hash) ? Path.Combine(Blobs, hash) : throw new InvalidOperationException("Invalid stored content hash.");
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private UploadManifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 8192) throw new InvalidOperationException("Recovery manifest exceeds its bound.");
        var value = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.UploadManifest) ?? throw new InvalidOperationException("Invalid recovery manifest.");
        if (value.Version != 1 || Path.GetFileName(path) != Canonical.Id(value.OperationId) + ".json" || value.ContentLength is < 0 or > MaximumAttachment ||
            value.ContentHash is not null && !ValidHash(value.ContentHash)) throw new InvalidOperationException("Invalid recovery manifest identity.");
        return value;
    }

    private void WriteManifest(UploadManifest value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, AppJson.Default.UploadManifest);
        if (bytes.Length > 8192) throw new InvalidOperationException("Recovery manifest exceeds its bound.");
        string path = ManifestPath(value.OperationId); string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true); DurableDirectory.Flush(Staging);
    }

    private void Release(string operation, long reservation)
    { lock (_gate) { if (!_active.Remove(operation)) throw new InvalidOperationException("Upload reservation released twice."); _reserved -= reservation; } }

    public void Dispose() => _processLock.Dispose();

    internal sealed class UploadLease : IAsyncDisposable
    {
        private readonly BlobStore _owner;
        private readonly long _reservation;
        private bool _released;
        private bool _databaseBinding;
        internal UploadManifest Manifest { get; private set; }
        internal UploadLease(BlobStore owner, UploadManifest manifest, long reservation) { _owner = owner; Manifest = manifest; _reservation = reservation; }
        internal void BeginDatabaseBinding() => _databaseBinding = true;

        internal async Task StageAsync(Stream input, CancellationToken token)
        {
            string part = _owner.PartPath(Manifest.OperationId);
            string hash;
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32_768, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var sink = new HashingSink(file, digest, Manifest.ContentLength);
                // Native Server's CopyToAsync charges its 32KiB managed staging and native overlap.
                await input.CopyToAsync(sink, 32_768, token).ConfigureAwait(false);
                if (sink.Count != Manifest.ContentLength) throw new ApplicationProblem(400, "attachment_length_mismatch");
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
                hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            }
            // Persist the recovery hash before rename; a crash at every later point keeps the blob protected.
            Manifest = Manifest with { ContentHash = hash, State = "renaming" };
            _owner.WriteManifest(Manifest);
            lock (_owner._gate)
            {
                string destination = _owner.BlobPath(hash);
                if (File.Exists(destination))
                {
                    if (new FileInfo(destination).Length != Manifest.ContentLength) throw new InvalidOperationException("Immutable content identity collision.");
                    using var existing = File.OpenRead(destination);
                    if (Convert.ToHexStringLower(SHA256.HashData(existing)) != hash) throw new InvalidOperationException("Immutable blob corruption.");
                    File.Delete(part);
                }
                else File.Move(part, destination);
                DurableDirectory.Flush(_owner.Blobs); DurableDirectory.Flush(_owner.Staging);
            }
            Manifest = Manifest with { State = "prepared" }; _owner.WriteManifest(Manifest);
        }

        internal void Complete(Replay result)
        {
            if (result.Status == 503) { Manifest = Manifest with { State = "recovery" }; _owner.WriteManifest(Manifest); }
            else _owner.ConfirmRecoveredUpload(Manifest.OperationId);
        }

        public ValueTask DisposeAsync()
        {
            if (_released) return ValueTask.CompletedTask;
            _released = true;
            try
            {
                if (Manifest.ContentHash is null)
                {
                    // No database operation was admitted; the incomplete stage remains visible for grace cleanup.
                    Manifest = Manifest with { State = "abandoned" }; _owner.WriteManifest(Manifest);
                }
                else if (!_databaseBinding) _owner.ConfirmRecoveredUpload(Manifest.OperationId);
            }
            catch (IOException) { Console.Error.WriteLine("upload_manifest_cleanup_pending"); }
            finally { _owner.Release(Manifest.OperationId, _reservation); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HashingSink(FileStream file, IncrementalHash hash, long maximum) : Stream
    {
        internal long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > 32_768 || buffer.Length > maximum - Count) throw new ApplicationProblem(413, "attachment_limit");
            await file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); hash.AppendData(buffer.Span); Count += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => file.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Platform-local directory durability after immutable renames; no claim of power-loss testing.</summary>
internal static partial class DurableDirectory
{
    internal static void Flush(string directory)
    {
        if (OperatingSystem.IsLinux())
        {
            int descriptor = Open(directory, 0x10000 | 0x80000); // O_DIRECTORY | O_CLOEXEC, read-only.
            if (descriptor < 0) throw new IOException("Directory open failed for durability.");
            try { if (Fsync(descriptor) != 0) throw new IOException("Directory fsync failed."); }
            finally { _ = Close(descriptor); }
        }
        else if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(directory, 0x80000000, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid || !FlushFileBuffers(handle)) throw new IOException("Directory durability is unsupported by this Windows filesystem profile.");
        }
        else throw new PlatformNotSupportedException("The reference blob persistence profile requires explicit platform validation.");
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int fd);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool FlushFileBuffers(SafeFileHandle handle);
}
