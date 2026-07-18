using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ApiKeys.UpdateApiKey;

/// <summary>
/// Data transfer object for api key update.
/// </summary>
/// <remarks>
/// Only metadata and permissions can be changed. The key itself, its expiry and its signing version are fixed at
/// creation - changing them would mean issuing a different key, which is what the create endpoint is for.
/// </remarks>
public class UpdateApiKeyCommand : MilvaionBaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Human readable name of the key.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Description explaining what the key is used for.
    /// </summary>
    public UpdateProperty<string> Description { get; set; }

    /// <summary>
    /// Related entities will always be updated according to the values in this list. If you send it empty, related
    /// entities will be cleared. If no update has been made, please send it with isUpdated false.
    /// </summary>
    public UpdateProperty<List<int>> PermissionIdList { get; set; }
}
