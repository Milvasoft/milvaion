using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Create content model.
/// </summary>
public record UpsertMediaDto
{
    /// <summary>
    /// Media of the content as base64 string.
    /// </summary>
    public string MediaAsBase64 { get; set; }

    /// <summary>
    /// Media of the content.
    /// </summary>
    [JsonIgnore]
    public byte[] Media { get => MediaAsBase64 != null ? MilvaionExtensions.DataUriToPlainText(MediaAsBase64) : []; }

    /// <summary>
    /// Media type. etc. "image", "video", "audio"
    /// </summary>
    public string Type { get; set; }
}
