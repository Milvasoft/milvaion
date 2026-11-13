using Milvaion.Application.Interfaces.Secrets;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Masking helpers for the messages workers send back.
/// </summary>
public static class SecretMaskerExtensions
{
    /// <summary>
    /// Masks the message and the data of a worker log in place.
    /// </summary>
    public static void MaskLog(this ISecretMasker masker, OccurrenceLog log)
    {
        if (masker == null || log == null)
            return;

        log.Message = masker.Mask(log.Message);
        log.Data = masker.MaskData(log.Data);
    }

    /// <summary>
    /// Masks the values of structured log data. The data is round-tripped through JSON only when masking
    /// changed something, so the common case costs one serialization.
    /// </summary>
    public static Dictionary<string, object> MaskData(this ISecretMasker masker, Dictionary<string, object> data)
    {
        if (masker == null || data == null || data.Count == 0)
            return data;

        var json = JsonSerializer.Serialize(data);
        var masked = masker.Mask(json);

        if (ReferenceEquals(json, masked) || json == masked)
            return data;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(masked) ?? data;
        }
        catch (JsonException)
        {
            // A replacement inside a key or an escape sequence made the document unreadable. Keep nothing
            // rather than the original, which holds the value.
            return new Dictionary<string, object> { ["masked"] = "Log data removed because it contained a secret value." };
        }
    }
}
