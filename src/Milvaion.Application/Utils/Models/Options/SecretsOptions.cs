namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Settings for secret references in job data. Bound from <c>MilvaionConfig:Secrets</c>.
/// </summary>
public class SecretsOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Secrets";

    /// <summary>
    /// When false, references are left in the job data untouched and workers receive the literal
    /// <c>{{secret:name}}</c> text. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The providers to consult, in order, for a reference that does not name one. Names are
    /// case-insensitive: <c>env</c>, <c>config</c>, <c>vault</c>, <c>azurekeyvault</c>.
    /// Default: environment variables, then configuration. Vault and Azure Key Vault are used only
    /// when listed here or named in a reference, and only when configured.
    /// </summary>
    public List<string> Providers { get; set; } = ["env", "config"];

    /// <summary>
    /// How long a resolved value is kept in memory before the provider is asked again. Default 60 seconds;
    /// 0 disables the cache.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>
    /// How long a resolved value is remembered for masking text that workers send back (logs, exceptions,
    /// dead-lettered job data). Default 24 hours. Only the API instance that resolved a value can mask it.
    /// </summary>
    public int MaskRetentionMinutes { get; set; } = 1440;

    /// <summary>
    /// How long the unresolved job data of a dispatched occurrence is kept in Redis, so a failed occurrence
    /// record can store the template instead of the resolved data the worker sends back. Default 7 days.
    /// </summary>
    public int TemplateRetentionHours { get; set; } = 168;

    /// <summary>
    /// Values shorter than this are not masked, since replacing a two-letter value everywhere in a log
    /// would do more harm than good. Default 4.
    /// </summary>
    public int MinimumMaskLength { get; set; } = 4;

    /// <summary>
    /// Settings for the environment variable provider (<c>env</c>).
    /// </summary>
    public EnvironmentSecretsOptions Environment { get; set; } = new();

    /// <summary>
    /// Secrets kept in configuration (<c>config</c> provider). Key is the secret name. Supply them through
    /// environment variables or a mounted file rather than appsettings.json, for example
    /// <c>MilvaionConfig__Secrets__Values__smtp-password</c>.
    /// </summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HashiCorp Vault KV version 2 settings (<c>vault</c> provider).
    /// </summary>
    public VaultSecretsOptions Vault { get; set; } = new();

    /// <summary>
    /// Azure Key Vault settings (<c>azurekeyvault</c> provider).
    /// </summary>
    public AzureKeyVaultSecretsOptions AzureKeyVault { get; set; } = new();
}

/// <summary>
/// Environment variable provider settings.
/// </summary>
public class EnvironmentSecretsOptions
{
    /// <summary>
    /// Prefix of the variables. <c>{{secret:smtp-password}}</c> reads <c>MILVAION_SECRET_SMTP_PASSWORD</c>:
    /// the name is upper-cased and every character other than a letter or digit becomes an underscore.
    /// </summary>
    public string Prefix { get; set; } = "MILVAION_SECRET_";
}

/// <summary>
/// HashiCorp Vault KV version 2 settings.
/// </summary>
public class VaultSecretsOptions
{
    /// <summary>
    /// Vault address, for example <c>https://vault.example.com:8200</c>. The provider is off while empty.
    /// </summary>
    public string Address { get; set; }

    /// <summary>
    /// Vault token. Prefer <see cref="TokenFile"/> or the <c>VAULT_TOKEN</c> environment variable.
    /// </summary>
    public string Token { get; set; }

    /// <summary>
    /// Path of a file holding the token, such as one written by Vault Agent. Read on every token use, so a
    /// renewed token is picked up.
    /// </summary>
    public string TokenFile { get; set; }

    /// <summary>
    /// Vault Enterprise namespace, sent as <c>X-Vault-Namespace</c>. Optional.
    /// </summary>
    public string Namespace { get; set; }

    /// <summary>
    /// Mount path of the KV v2 engine. Default <c>secret</c>.
    /// </summary>
    public string MountPath { get; set; } = "secret";

    /// <summary>
    /// Key read when a reference has no <c>#key</c> part. Default <c>value</c>.
    /// </summary>
    public string DefaultKey { get; set; } = "value";

    /// <summary>
    /// Request timeout in seconds. Default 10.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Azure Key Vault settings.
/// </summary>
public class AzureKeyVaultSecretsOptions
{
    /// <summary>
    /// Vault URI, for example <c>https://my-vault.vault.azure.net/</c>. The provider is off while empty.
    /// </summary>
    public string VaultUri { get; set; }

    /// <summary>
    /// Tenant of a service principal. When <see cref="TenantId"/>, <see cref="ClientId"/> and
    /// <see cref="ClientSecret"/> are all set, that principal is used; otherwise <c>DefaultAzureCredential</c>
    /// (managed identity, workload identity, environment variables, Azure CLI).
    /// </summary>
    public string TenantId { get; set; }

    /// <summary>
    /// Client id of a service principal, or of a user-assigned managed identity when no secret is given.
    /// </summary>
    public string ClientId { get; set; }

    /// <summary>
    /// Client secret of a service principal.
    /// </summary>
    public string ClientSecret { get; set; }
}
