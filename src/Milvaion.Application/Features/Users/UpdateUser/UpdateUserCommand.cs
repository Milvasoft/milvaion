using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.Users.UpdateUser;

/// <summary>
/// Data transfer object for user update.
/// </summary>
public class UpdateUserCommand : MilvaionBaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Name of user.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Surname of user.
    /// </summary>
    public UpdateProperty<string> Surname { get; set; }

    /// <summary>
    /// If the user enters the wrong password repeatedly, the account will be locked for a certain period of time. 
    /// If this lock is to be removed, false should be sent, if this lock is to be placed on the user for a certain period of time, true should be sent.
    /// </summary>
    public UpdateProperty<bool> Lockout { get; set; }

    /// <summary>
    /// If a new password is to be assigned to the user, this field should be sent.
    /// </summary>
    public UpdateProperty<string> NewPassword { get; set; }

    /// <summary>
    /// Related entities will always be updated according to the values ​​in this list. If you send it empty, related entities will be cleared. 
    /// If no update has been made, please send it with isUpdated false.
    /// </summary>
    public UpdateProperty<List<int>> RoleIdList { get; set; }

    /// <summary>
    /// Allowed notification types for the user.
    /// </summary>
    public UpdateProperty<List<NotificationType>> AllowedNotifications { get; set; }
}
