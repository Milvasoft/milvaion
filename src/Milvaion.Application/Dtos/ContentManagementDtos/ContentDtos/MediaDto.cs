using Milvasoft.Attributes.Annotations;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Media model.
/// </summary>
public class MediaDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Media value.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public byte[] Value { get; set; }

    /// <summary>
    /// Media as base64 string.
    /// </summary>
    [LinkedWith<ByteArrayToBase64Formatter>(nameof(Value), ByteArrayToBase64Formatter.FormatterName)]
    [Filterable(false)]
    public string MediaAsBase64 { get; set; }

    /// <summary>
    /// Media type. etc. "image", "video", "audio"
    /// </summary>
    public string Type { get; set; }
}
