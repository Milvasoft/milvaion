namespace HttpWorker.Options;

/// <summary>
/// Configuration options for HTTP Worker. Defaults keep every job working as it did before these options existed:
/// restrictions are opt-in, and only the two size limits apply out of the box.
/// </summary>
public class HttpWorkerOptions
{
    /// <summary>
    /// Configuration section key in appsettings.json.
    /// </summary>
    public const string SectionKey = "HttpWorker";

    /// <summary>
    /// Most response body bytes read into memory. A longer body is cut off here and the job continues with what was read.
    /// 0 = no limit. Default: 100 MiB.
    /// </summary>
    public long MaxResponseBodyBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Largest file a multipart <c>ContentUrl</c> may download. A larger file fails the job.
    /// 0 = no limit. Default: 1 GiB.
    /// </summary>
    public long MaxFileDownloadBytes { get; set; } = 1024 * 1024 * 1024;

    /// <summary>
    /// Limits on what job data can make this worker do.
    /// </summary>
    public HttpWorkerSecurityOptions Security { get; set; } = new();
}

/// <summary>
/// Limits on what job data can make this worker do. Anyone who can create a job controls the URL, proxy and TLS
/// settings of its requests, and those requests come from inside the worker's network.
/// </summary>
/// <remarks>
/// Target rules are checked on every new connection, so they also cover redirects and hosts that resolve to a different
/// address later. When a request goes through a proxy only the proxy's address is seen at that point: the job's URL is
/// still checked up front, but its redirects are not, so set <see cref="AllowProxy"/> to false where that matters.
/// </remarks>
public class HttpWorkerSecurityOptions
{
    /// <summary>
    /// Hosts requests may go to, as exact names or wildcards like <c>*.example.com</c>. A proxy must be listed too.
    /// Empty = any host.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Refuses loopback, private, link-local (including the 169.254.169.254 cloud metadata service), carrier-grade NAT,
    /// multicast and unspecified addresses. Default: false.
    /// </summary>
    public bool BlockPrivateNetworks { get; set; }

    /// <summary>
    /// Additional address ranges to refuse, in CIDR notation.
    /// </summary>
    public List<string> BlockedCidrs { get; set; } = [];

    /// <summary>
    /// Address ranges that stay reachable despite <see cref="BlockPrivateNetworks"/> and <see cref="BlockedCidrs"/>.
    /// </summary>
    public List<string> AllowedCidrs { get; set; } = [];

    /// <summary>
    /// Whether job data may turn off TLS certificate validation. Default: true.
    /// </summary>
    public bool AllowIgnoreSslErrors { get; set; } = true;

    /// <summary>
    /// Whether job data may use a client certificate from this machine's certificate store by thumbprint. Default: true.
    /// </summary>
    public bool AllowThumbprintCertificates { get; set; } = true;

    /// <summary>
    /// Whether job data may route requests through a proxy. Default: true.
    /// </summary>
    public bool AllowProxy { get; set; } = true;
}
