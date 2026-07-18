using System.Text;

namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Api key configuration options.
/// </summary>
public class ApiKeyOptions
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:ApiKey";

    /// <summary>
    /// Secret used to sign api keys issued by this installation.
    /// Anyone holding it can mint keys, so treat it like a private key: keep it out of source control and
    /// supply it through an environment variable or secret store in production.
    /// </summary>
    public string Secret { get; set; }

    /// <summary>
    /// Current version of <see cref="Secret"/>. Stamped onto every key issued from now on.
    /// Increment it after rotating the secret so previously issued keys are rejected with a clear reason
    /// rather than silently failing signature validation.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// <see cref="Secret"/> as a byte array, for signing and validation.
    /// </summary>
    public byte[] SecretBytes => Encoding.ASCII.GetBytes(Secret);
}
