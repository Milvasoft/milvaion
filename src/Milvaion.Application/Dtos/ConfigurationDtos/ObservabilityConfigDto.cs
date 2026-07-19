namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Log ve metrik dışa aktarım ayarları.
/// </summary>
public class ObservabilityConfigDto
{
    /// <summary> Seq'e teknik log gönderimi. </summary>
    public SeqConfigDto Seq { get; set; }

    /// <summary> OpenTelemetry metrik dışa aktarımı. </summary>
    public OpenTelemetryConfigDto OpenTelemetry { get; set; }
}

/// <summary>
/// Seq bağlantısı.
/// </summary>
public class SeqConfigDto
{
    /// <summary> Gönderim açık mı. </summary>
    public bool Enabled { get; set; }

    /// <summary> Seq adresi. Kimlik bilgisi taşımıyor, o yüzden gösterilebiliyor. </summary>
    public string Uri { get; set; }
}

/// <summary>
/// OpenTelemetry dışa aktarımı.
/// </summary>
public class OpenTelemetryConfigDto
{
    /// <summary> Dışa aktarım açık mı. </summary>
    public bool Enabled { get; set; }

    /// <summary> Metriklerin sunulduğu yol. </summary>
    public string ExportPath { get; set; }

    /// <summary> Servis adı etiketi. </summary>
    public string Service { get; set; }

    /// <summary> Ortam etiketi. </summary>
    public string Environment { get; set; }

    /// <summary> İş etiketi. </summary>
    public string Job { get; set; }

    /// <summary> Örnek etiketi. </summary>
    public string Instance { get; set; }
}
