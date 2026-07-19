namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Uyarı gönderim ayarları.
/// </summary>
/// <remarks>
/// Kanal yapılandırmasının tamamı değil, yalnızca hangi kanalın açık olduğu ve
/// nereye gönderildiği. Webhook adresleri ve SMTP kimlik bilgileri bilerek
/// dışarıda: bu ekran yetkisi olan herkese açık ve bir webhook adresi, onu bilen
/// herkesin o kanala mesaj atabilmesi demek.
/// </remarks>
public class AlertingConfigDto
{
    /// <summary> Uyarı mesajlarındaki bağlantılarda kullanılan adres. </summary>
    public string MilvaionAppUrl { get; set; }

    /// <summary> Kanal belirtilmemiş uyarıların gittiği yer. </summary>
    public string DefaultChannel { get; set; }

    /// <summary> Yalnızca üretim ortamında gönderim. </summary>
    public bool SendOnlyInProduction { get; set; }

    /// <summary> Kanal başına durum. </summary>
    public List<AlertChannelStatusDto> Channels { get; set; } = [];

    /// <summary> Tanımlı uyarı türü sayısı. </summary>
    public int ConfiguredAlertCount { get; set; }

    /// <summary> Bunlardan kaçı açık. </summary>
    public int EnabledAlertCount { get; set; }
}

/// <summary>
/// Tek bir uyarı kanalının durumu.
/// </summary>
public class AlertChannelStatusDto
{
    /// <summary> Kanal adı. </summary>
    public string Name { get; set; }

    /// <summary> Kanal açık mı. </summary>
    public bool Enabled { get; set; }

    /// <summary> Yalnızca üretimde mi gönderiyor. </summary>
    public bool SendOnlyInProduction { get; set; }

    /// <summary> Varsayılan hedef - kanal ya da alan adı. Adres değil, ad. </summary>
    public string DefaultTarget { get; set; }
}
