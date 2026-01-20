using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the Users table.
/// </summary>
[Table(TableNames.UserSessions)]
[Index(nameof(UserName), nameof(DeviceId))]
[DontIndexCreationDate]
public class UserSession : AuditableEntity<long>
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
    /// Gets or sets the Ip address.
    /// </summary>
    public string IpAddress { get; set; }

    /// <summary>
    /// Related user id.
    /// </summary>
    [ForeignKey(nameof(User))]
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property of user relation.
    /// </summary>
    public virtual User User { get; set; }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class Conditions
    {
        public static Expression<Func<UserSession, bool>> CurrentSession(string userName, string deviceId) => s => s.UserName == userName && s.DeviceId == deviceId;
        public static Expression<Func<UserSession, bool>> DeleteAllSessions(string userName) => s => s.UserName == userName;
    }

    public static class Projections
    {
        public static Expression<Func<UserSession, UserSession>> CurrentSession { get; } = s => new UserSession
        {
            Id = s.Id,
            AccessToken = s.AccessToken,
            RefreshToken = s.RefreshToken,
            UserName = s.UserName,
            UserId = s.UserId,
            DeviceId = s.DeviceId,
            CreationDate = s.CreationDate,
            ExpiryDate = s.ExpiryDate,
            IpAddress = s.IpAddress,
        };
    }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
