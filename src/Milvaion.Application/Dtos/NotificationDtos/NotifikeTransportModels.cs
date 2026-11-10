namespace Milvaion.Application.Dtos.NotificationDtos;

/// <summary>
/// An e-mail to deliver through Notifike.
/// </summary>
public class NotifikeEmail
{
    /// <summary>Subject.</summary>
    public string Subject { get; set; }

    /// <summary>HTML body.</summary>
    public string HtmlBody { get; set; }

    /// <summary>E-mail addresses.</summary>
    public List<string> Recipients { get; set; } = [];

    /// <summary>Whether Notifike should send it ahead of normal traffic.</summary>
    public bool IsHighPriority { get; set; }

    /// <summary>Free label stored with the message, the alert type for example.</summary>
    public string Tag { get; set; }
}

/// <summary>
/// An in-app notification to put in the Notifike boxes of users.
/// </summary>
public class NotifikeInAppNotification
{
    /// <summary>Kind of the notification.</summary>
    public string Type { get; set; }

    /// <summary>Text; may be null when clients render it from the type and the data.</summary>
    public string Text { get; set; }

    /// <summary>Address opened when the notification is tapped.</summary>
    public string Link { get; set; }

    /// <summary>Kind of the record the notification is about.</summary>
    public string EntityType { get; set; }

    /// <summary>Id of the record the notification is about.</summary>
    public string EntityId { get; set; }

    /// <summary>Extra data as JSON. Sent only when it is a JSON object within Notifike's limit.</summary>
    public string DataJson { get; set; }

    /// <summary>User names of the recipients.</summary>
    public List<string> UserKeys { get; set; } = [];
}

/// <summary>
/// Outcome of a delivery through Notifike.
/// </summary>
/// <param name="Success">Whether Notifike accepted the delivery.</param>
/// <param name="Error">Why it failed; carries no credentials.</param>
public record NotifikeTransportResult(bool Success, string Error = null);
