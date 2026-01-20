using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.Languages.UpdateLanguage;

/// <summary>
/// Data transfer object for role update.
/// </summary>
public class UpdateLanguageCommand : MilvaionBaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Determines whether language is default or not.
    /// </summary>
    public UpdateProperty<bool> IsDefault { get; set; }

    /// <summary>
    /// Determines whether language is supported or not.
    /// </summary>
    public UpdateProperty<bool> Supported { get; set; }
}
