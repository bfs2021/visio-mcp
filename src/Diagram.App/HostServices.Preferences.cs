using System.Text.Json;
using System.Text.Json.Nodes;
using Diagram.Core.Contracts;
using Diagram.Host.Core;

namespace Diagram.App;

public sealed partial class HostServices
{
    readonly SemaphoreSlim preferenceSaveGate = new(1, 1);
    static JsonObject PreferencePayload(PreferencesSnapshot snapshot) => new()
    {
        ["preferences"] = JsonSerializer.SerializeToNode(snapshot.Preferences, ContractJson.Options),
        ["versionTag"] = snapshot.VersionTag,
        ["diagnostic"] = snapshot.Diagnostic,
    };

    async Task<ResponseEnvelope> PreferencesAsync(RequestEnvelope request, CancellationToken ct)
    {
        if (request.Method == "host.preferences.get")
            return Ok(request, PreferencePayload(await window.Preferences.ReadAsync(ct)));
        if (request.Method != "host.preferences.save")
            return Err(request, "method_not_found", $"unknown host method {request.Method}");

        var validated = PreferencesValidation.ValidatePreferences(request.Params["preferences"]);
        if (validated.Value is null) return Err(request, validated.Error!);
        if (request.Params["expectedVersionTag"] is not JsonValue tagNode || !tagNode.TryGetValue<string>(out var tag) || string.IsNullOrWhiteSpace(tag))
            return Err(request, "invalid_request", "expectedVersionTag is required");
        var reset = false;
        if (request.Params["resetInvalid"] is { } resetNode)
        {
            if (resetNode is not JsonValue resetValue || !resetValue.TryGetValue<bool>(out reset))
                return Err(request, "invalid_request", "resetInvalid must be a boolean");
        }

        await preferenceSaveGate.WaitAsync(ct);
        PreferencesSaveResult saved;
        try
        {
            saved = await window.Preferences.SaveAsync(validated.Value, tag, reset, ct);
            if (saved.Status == PreferencesSaveStatus.Success)
            {
                // Once the file is durable, request cancellation must not leave this window on the old cadence.
                await window.RecoveryMgr.UpdateCheckpointIntervalAsync(TimeSpan.FromSeconds(saved.Snapshot.Preferences.Recovery.CheckpointSeconds));
                return Ok(request, PreferencePayload(saved.Snapshot));
            }
        }
        finally { preferenceSaveGate.Release(); }
        var code = saved.Status switch
        {
            PreferencesSaveStatus.Conflict => "revision_conflict",
            PreferencesSaveStatus.IoError => "io_error",
            _ => "invalid_request",
        };
        var details = PreferencePayload(saved.Snapshot);
        if (saved.Status == PreferencesSaveStatus.Conflict) details["reason"] = "preferences_changed";
        return Err(request, new AppError
        {
            Code = code,
            Message = saved.Error ?? "Preferences could not be saved.",
            Retryable = saved.Status == PreferencesSaveStatus.Conflict,
            Details = details,
        });
    }
}
