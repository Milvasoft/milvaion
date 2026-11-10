using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Milvaion.Api.Controllers;
using System.Reflection;

namespace Milvaion.Api.AppStartup;

/// <summary>
/// Keeps <see cref="DeveloperController"/> out of the application unless it is explicitly enabled, so that its
/// routes do not exist on a default deployment rather than existing and being guarded.
/// </summary>
/// <param name="enabled">Value of <c>MilvaionConfig:Developer:EndpointsEnabled</c>.</param>
public sealed class DeveloperEndpointsFeatureProvider(bool enabled) : IApplicationFeatureProvider<ControllerFeature>
{
    private readonly bool _enabled = enabled;

    /// <inheritdoc/>
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (!_enabled)
            feature.Controllers.Remove(typeof(DeveloperController).GetTypeInfo());
    }
}
