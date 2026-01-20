namespace Milvaion.Domain.JsonModels;

/// <summary>
/// Contains api request information.
/// </summary>
public class RequestInfo
{
    /// <summary>
    /// Http method of request.
    /// </summary>
    public string Method { get; set; }

    /// <summary>
    /// Absolute uri of request.
    /// </summary>
    public string AbsoluteUri { get; set; }

    /// <summary>
    /// Query string of request.
    /// </summary>
    public string QueryString { get; set; }

    /// <summary>
    /// Request headers.
    /// </summary>
    public object Headers { get; set; }

    /// <summary>
    /// Request length.
    /// </summary>
    public long ContentLength { get; set; }

    /// <summary>
    /// Request body.
    /// </summary>
    public object Body { get; set; }
}
