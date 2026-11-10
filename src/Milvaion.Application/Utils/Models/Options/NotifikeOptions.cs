namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Optional delivery of Milvaion's e-mails and internal notifications through Notifike, the company's notification service.
/// </summary>
public class NotifikeOptions
{
    /// <summary>
    /// Configuration section key in appsettings.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Notifike";

    /// <summary>
    /// Gets or sets whether Notifike is used at all. Off by default, so the local delivery paths stay in effect.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets whether Notifike is used only in the production environment.
    /// </summary>
    public bool SendOnlyInProduction { get; set; } = true;

    /// <summary>
    /// Gets or sets the base address of the Notifike API. Example: "https://api.notifike.example.com"
    /// </summary>
    public string BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the API key of the Notifike application. A secret: never log or return it.
    /// </summary>
    public string ApiKey { get; set; }

    /// <summary>
    /// Gets or sets whether e-mail alerts are sent through Notifike instead of SMTP.
    /// </summary>
    public bool UseForEmail { get; set; } = true;

    /// <summary>
    /// Gets or sets whether internal notifications are also put in the recipients' Notifike in-app boxes.
    /// </summary>
    public bool UseForInternalNotifications { get; set; } = true;
}
