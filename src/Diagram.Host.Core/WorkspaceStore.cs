using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diagram.Host.Core;

/// <summary>One tab as remembered between runs. <c>SessionId</c> names its recovery session; <c>Path</c> is the file it came from or was saved to.</summary>
public sealed record WorkspaceTab(string DocumentId, string SessionId, string? Path, string? Title, bool Unsaved);

/// <summary>The open tabs in order and which was active. <c>CleanExit</c> is false while the app runs and true only after a normal shutdown.</summary>
public sealed record WorkspaceState(bool CleanExit, IReadOnlyList<WorkspaceTab> Tabs, string? ActiveDocumentId);

/// <summary>
/// Remembers which documents were open (<c>workspace.json</c>) so the next start can reopen them. It holds no document content: saved files are
/// read from disk and unsaved work comes back from its recovery session. Written atomically; an unreadable file is treated as absent.
/// </summary>
public sealed class WorkspaceStore(string file, AtomicFileWriter writer)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    readonly SemaphoreSlim gate = new(1, 1);

    public string File => file;

    public async Task<WorkspaceState?> ReadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadUnlockedAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    async Task<WorkspaceState?> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(file)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceState>(await System.IO.File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json);
            return state is { Tabs: not null } ? state : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Records the tabs as they are now. The app is running, so this is not a clean exit.</summary>
    public async Task WriteAsync(IReadOnlyList<WorkspaceTab> tabs, string? activeDocumentId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await writer.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(new WorkspaceState(false, tabs, activeDocumentId), Json), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>The app is closing normally: remember that, so the next start does not report an unexpected exit.</summary>
    public async Task MarkCleanExitAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReadUnlockedAsync(ct).ConfigureAwait(false) is not { } state) return;
            await writer.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(state with { CleanExit = true }, Json), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
