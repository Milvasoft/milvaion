using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ExportDtos;

/// <summary>
/// Data transfer object for export.
/// </summary>
[ExcludeFromMetadata]
public class ExportResult : BaseDto<int>
{
    /// <summary>
    /// File stream.
    /// </summary>
    [JsonIgnore]
    public Stream FileStream { get; set; }

    /// <summary>
    /// File extension.
    /// </summary>
    public string MimeType { get; set; }

    /// <summary>
    /// File extension.
    /// </summary>
    public string FileName { get; set; }
}
