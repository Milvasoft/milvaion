namespace Milvaion.Domain.JsonModels;

/// <summary>
/// Information returned by the API in response to the request.
/// </summary>
public class ResponseInfo
{
    /// <summary>
    /// Status code of response.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Response headers.
    /// </summary>
    public object Headers { get; set; }

    /// <summary>
    /// Response length.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Response body.
    /// </summary>
    public object Body { get; set; }

    /// <summary>
    /// Response content type.
    /// </summary>
    public string ContentType { get; set; }
}
