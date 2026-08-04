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
/// Admin gating is per-method rather than on the controller: <see cref="UserTypeAuthAttribute"/>
/// is a plain authorization filter that does not honour <c>[AllowAnonymous]</c>, so a
/// class-level attribute would also block the public endpoint. Keeping it per-method leaves the
/// public endpoint genuinely open.
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
    [UserTypeAuth(UserType.Manager)]
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
    [UserTypeAuth(UserType.Manager)]
    [HttpPut]
    public Task<Response<bool>> UpdateSettingsAsync(UpdateSettingsCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
