using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the Users table.
/// </summary>
[Table(TableNames.UserSessionHistories)]
[DontIndexCreationDate]
public class UserSessionHistory : AuditableEntity<long>
{
    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    public string AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the refresh token.
    /// </summary>
    public string RefreshToken { get; set; }

    /// <summary>
    /// Gets or sets the expiry date.
    /// </summary>
    public DateTime ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the device ID.
    /// </summary>
    public string DeviceId { get; set; }

    /// <summary>
    /// Related user id.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the Ip address.
    /// </summary>
    public string IpAddress { get; set; }

    /// <summary>
    /// Initializes new instance of <see cref="UserSessionHistory"/>.
    /// </summary>
    public UserSessionHistory()
    {
    }

    /// <summary>
    /// Initializes new instance of <see cref="UserSessionHistory"/> with given <paramref name="session"/>.
    /// </summary>
    /// <param name="session"></param>
    public UserSessionHistory(UserSession session)
    {
        UserId = session.UserId;
        UserName = session.UserName;
        AccessToken = session.AccessToken;
        RefreshToken = session.RefreshToken;
        ExpiryDate = session.ExpiryDate;
        DeviceId = session.DeviceId;
        IpAddress = session.IpAddress;
        CreationDate = session.CreationDate;
    }
}
