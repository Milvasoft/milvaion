using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.SettingsDtos;
using Milvaion.Application.Features.Settings.GetPublicSettings;
using Milvaion.Application.Features.Settings.GetSettings;
using Milvaion.Application.Features.Settings.UpdateSettings;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Application settings endpoints. Admin reads/writes the full document; a small public subset
/// (branding text) is exposed anonymously for the login page and browser tab.
/// </summary>
/// <remarks>
/// Auth is per-method rather than on the controller so the public branding endpoint can stay
/// anonymous: the admin read/write require the <c>SystemAdministration</c> permission, while the
/// public subset is reached with <c>[AllowAnonymous]</c>.
/// </remarks>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
public class SettingsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets everything the settings page shows: branding, notification rules, channel status and
    /// the available channels - one call for the whole page.
    /// </summary>
    [Auth(PermissionCatalog.SystemAdministration.Detail)]
    [HttpGet]
    public Task<Response<SettingsDto>> GetSettingsAsync(CancellationToken cancellation) => _mediator.Send(new GetSettingsQuery(), cancellation);

    /// <summary>
    /// Gets the non-secret settings subset (branding text) needed before authentication.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("public")]
    public Task<Response<PublicSettingsDto>> GetPublicSettingsAsync(CancellationToken cancellation) => _mediator.Send(new GetPublicSettingsQuery(), cancellation);

    /// <summary>
    /// Updates the application settings. Takes effect at runtime across all instances.
    /// </summary>
    [Auth(PermissionCatalog.SystemAdministration.Update)]
    [HttpPut]
    public Task<Response<bool>> UpdateSettingsAsync(UpdateSettingsCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
