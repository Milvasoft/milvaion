using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.NotificationDtos.GoogleChat;

/// <summary>
/// Google Chat space enumeration.
/// </summary>
public enum GoogleChatSpace
{
    /// <summary>
    /// Hub Notifications space.
    /// </summary>
    HubNotifications,

    /// <summary>
    /// Monitoring Alerts space.
    /// </summary>
    MonitoringAlerts,
}

/// <summary>
/// Thread information for message grouping.
/// </summary>
public class GoogleChatThread
{
    /// <summary>
    /// Thread key to group messages in the same thread.
    /// </summary>
    [JsonPropertyName("threadKey")]
    public string ThreadKey { get; set; }

    /// <summary>
    /// For reply existsing thread, the name of the thread.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }
}

/// <summary>
/// The root model for a Google Chat Card V2 message payload.
/// </summary>
public class GoogleChatNotification
{
    /// <summary>
    /// Belonging Google Chat space.
    /// </summary>
    public GoogleChatSpace Space { get; set; }

    /// <summary>
    /// Card message payload to be sent.
    /// </summary>
    public GoogleChatCardMessage CardMessage { get; set; }
}

/// <summary>
/// The root model for a Google Chat Card V2 message payload.
/// </summary>
public class GoogleChatCardMessage
{
    /// <summary>
    /// Cards V2 collection to be sent in the message.
    /// </summary>
    [JsonPropertyName("cardsV2")]
    public List<CardContainer> CardsV2 { get; set; }

    /// <summary>
    /// Optional. Specifies the thread to which the message belongs.
    /// </summary>
    [JsonPropertyName("thread")]
    public GoogleChatThread Thread { get; set; }
}

/// <summary>
/// A container wrapper for a single Card V2 object.
/// </summary>
public class CardContainer
{
    /// <summary>
    /// Card identifier.
    /// </summary>
    [JsonPropertyName("cardId")]
    public string CardId { get; set; }

    /// <summary>
    /// Card object containing header and sections.
    /// </summary>
    [JsonPropertyName("card")]
    public Card Card { get; set; }
}

/// <summary>
/// Defines the structure of the main Card object, including the header and sections.
/// </summary>
public class Card
{
    /// <summary>
    /// Card header information.
    /// </summary>
    [JsonPropertyName("header")]
    public CardHeader Header { get; set; }

    /// <summary>
    /// Sections within the card, each containing widgets.
    /// </summary>
    [JsonPropertyName("sections")]
    public List<Section> Sections { get; set; }
}

/// <summary>
/// Defines the header section of the card, including title, subtitle, and image details.
/// </summary>
public class CardHeader
{
    /// <summary>
    /// Title of the card.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; }

    /// <summary>
    /// Subtitle of the card.
    /// </summary>
    [JsonPropertyName("subtitle")]
    public string Subtitle { get; set; }

    /// <summary>
    /// Image URL for the card header.
    /// </summary>
    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; }

    /// <summary>
    /// Image type (e.g., CIRCLE, SQUARE).
    /// </summary>
    [JsonPropertyName("imageType")]
    public string ImageType { get; set; }
}

/// <summary>
/// Defines a section within the card, which holds a collection of widgets.
/// </summary>
public class Section
{
    /// <summary>
    /// Header text for the section.
    /// </summary>
    [JsonPropertyName("header")]
    public string Header { get; set; }

    /// <summary>
    /// Widgets contained in the section.
    /// </summary>
    [JsonPropertyName("widgets")]
    public List<Widget> Widgets { get; set; }
}

/// <summary>
/// Represents a single visual component (widget) within a card section.
/// </summary>
public class Widget
{
    /// <summary>
    /// Text content with optional leading icon and formatting.
    /// </summary>
    [JsonPropertyName("decoratedText")]
    public DecoratedText DecoratedText { get; set; }

    /// <summary>
    /// Button list for user interactions.
    /// </summary>
    [JsonPropertyName("buttonList")]
    public ButtonList ButtonList { get; set; }
}

/// <summary>
/// Text content with optional leading icon and formatting.
/// </summary>
public class DecoratedText
{
    /// <summary>
    /// Start icon displayed before the text.
    /// </summary>
    [JsonPropertyName("startIcon")]
    public Icon StartIcon { get; set; }

    /// <summary>
    /// The main text content, which can include HTML formatting.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; }
}

/// <summary>
/// Defines a built-in icon for a widget. For known icons see https://developers.google.com/workspace/chat/format-messages#builtinicons.
/// </summary>
public class Icon
{
    /// <summary>
    /// Known icon name (e.g., "STAR", "CLOCK", "MEMBERSHIP").
    /// </summary>
    [JsonPropertyName("knownIcon")]
    public string KnownIcon { get; set; }

    /// <summary>
    /// Material icon name.
    /// </summary>
    [JsonPropertyName("materialIcon")]
    public MaterialIcon MaterialIcon { get; set; }
}

/// <summary>
/// Custom icon.
/// </summary>
public class MaterialIcon
{
    /// <summary>
    /// Material icon name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// Material icon fill.
    /// </summary>
    [JsonPropertyName("fill")]
    public bool Fill { get; set; } = true;

    /// <summary>
    /// Material icon weight.
    /// </summary>
    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 300;

    /// <summary>
    /// Material icon grade.
    /// </summary>
    [JsonPropertyName("grade")]
    public int Grade { get; set; } = -25;
}

/// <summary>
/// A collection of buttons laid out horizontally.
/// </summary>
public class ButtonList
{
    /// <summary>
    /// Buttons included in the button list.
    /// </summary>
    [JsonPropertyName("buttons")]
    public List<Button> Buttons { get; set; }
}

/// <summary>
/// A clickable button with text.
/// </summary>
public class Button
{
    /// <summary>
    /// The text displayed on the button.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; set; }

    /// <summary>
    /// OnClick action defining what happens when the button is clicked.
    /// </summary>
    [JsonPropertyName("onClick")]
    public OnClick OnClick { get; set; }
}

/// <summary>
/// Defines the action when a component is clicked.
/// </summary>
public class OnClick
{
    /// <summary>
    /// OpenLink action to open a URL.
    /// </summary>
    [JsonPropertyName("openLink")]
    public OpenLink OpenLink { get; set; }
}

/// <summary>
/// Defines a URL to open when the link is clicked.
/// </summary>
public class OpenLink
{
    /// <summary>
    /// URL to be opened.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; }
}