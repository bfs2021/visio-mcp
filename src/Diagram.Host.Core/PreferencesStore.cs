using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

public sealed record PreferencesSnapshot(AppPreferences Preferences, string VersionTag, string? Diagnostic);
public enum PreferencesSaveStatus { Success, Conflict, IoError, ValidationError }
public sealed record PreferencesSaveResult(PreferencesSaveStatus Status, PreferencesSnapshot Snapshot, string? Error = null);

/// <summary>Host-owned per-user settings. Reads never repair files; replacement requires the exact original content tag.</summary>
public sealed class PreferencesStore(string file, AtomicFileWriter writer)
{
    sealed class ChangedDuringWrite(PreferencesSnapshot snapshot) : Exception
    {
        public PreferencesSnapshot Snapshot { get; } = snapshot;
    }
    const int MaxBytes = 64 * 1024;
    readonly SemaphoreSlim gate = new(1, 1);
    PreferencesSnapshot effective = new(PreferencesValidation.DefaultPreferences(), "missing", null);
    public string File { get; } = Path.GetFullPath(file);
    /// <summary>Fault-injection hook before creating an invalid source's backup.</summary>
    public Action? BeforeBackup { get; set; }

    public async Task<PreferencesSnapshot> ReadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return effective = await ReadUnlockedAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    async Task<PreferencesSnapshot> ReadUnlockedAsync(CancellationToken ct)
    {
        var defaults = PreferencesValidation.DefaultPreferences();
        try
        {
            await using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var prefix = new byte[MaxBytes + 1]; var used = 0; var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                var take = Math.Min(count, prefix.Length - used);
                buffer.AsSpan(0, take).CopyTo(prefix.AsSpan(used)); used += take;
            }
            var tag = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (used > MaxBytes) return new(defaults, tag, "Preferences exceed the 64 KiB limit.");
            try
            {
                var valid = PreferencesValidation.ValidatePreferences(JsonNode.Parse(prefix.AsSpan(0, used)));
                return valid.Value is { } value ? new(value, tag, null) : new(defaults, tag, "Preferences are invalid or use an unsupported version.");
            }
            catch (JsonException) { return new(defaults, tag, "Preferences could not be parsed."); }
        }
        catch (FileNotFoundException) { return new(defaults, "missing", null); }
        catch (DirectoryNotFoundException) { return new(defaults, "missing", null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(defaults, "unreadable", "Preferences could not be read."); }
    }

    public async Task<PreferencesSaveResult> SaveAsync(AppPreferences preferences, string expectedVersionTag, bool resetInvalid, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var published = effective;
        var current = published;
        try
        {
            PreferencesValidation.ValidationResult validated;
            try { validated = PreferencesValidation.ValidatePreferences(JsonSerializer.SerializeToNode(preferences, ContractJson.Options)); }
            catch (Exception e) when (e is JsonException or ArgumentException) { return new(PreferencesSaveStatus.ValidationError, published, "Invalid preferences."); }
            if (validated.Value is null || string.IsNullOrWhiteSpace(expectedVersionTag)) return new(PreferencesSaveStatus.ValidationError, published, "Invalid preferences or version tag.");
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            await using var lease = await AcquireLockAsync(ct).ConfigureAwait(false);
            current = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            if (current.VersionTag == "unreadable") return new(PreferencesSaveStatus.IoError, published, "The original preferences cannot be read or backed up.");
            if (!string.Equals(current.VersionTag, expectedVersionTag, StringComparison.Ordinal)) return new(PreferencesSaveStatus.Conflict, current, "Preferences changed; reload before saving.");
            if (current.Diagnostic is not null)
            {
                if (!resetInvalid) return new(PreferencesSaveStatus.ValidationError, current, "Reset must be explicit to replace invalid preferences.");
                await BackupAsync(ct).ConfigureAwait(false);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(validated.Value, ContractJson.Options);
            // The writer invokes this after its fault hook and immediately before replacement.
            await writer.WriteAsync(File, bytes, ct, async token =>
            {
                token.ThrowIfCancellationRequested();
                var latest = await ReadUnlockedAsync(token).ConfigureAwait(false);
                if (!string.Equals(latest.VersionTag, current.VersionTag, StringComparison.Ordinal)) throw new ChangedDuringWrite(latest);
            }).ConfigureAwait(false);
            effective = new(validated.Value, BlobStore.HashOf(bytes), null);
            return new(PreferencesSaveStatus.Success, effective);
        }
        catch (ChangedDuringWrite e)
        { return new(PreferencesSaveStatus.Conflict, e.Snapshot, "Preferences changed; reload before saving."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(PreferencesSaveStatus.IoError, published, "Preferences could not be saved."); }
        finally { gate.Release(); }
    }

    async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(File + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
    }

    async Task BackupAsync(CancellationToken ct)
    {
        var backup = File + "." + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            await using var source = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            BeforeBackup?.Invoke();
            await using var destination = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, 8192, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false); destination.Flush(true);
        }
        catch
        {
            try { System.IO.File.Delete(backup); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
