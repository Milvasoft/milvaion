using Microsoft.Extensions.DependencyInjection;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Decorator;

namespace Milvaion.Application.Utils.Aspects.UserActivityLogAspect;

/// <summary>
/// Specifies that the method marked with this attribute will be added to as activity to database by the <see cref="UserActivityLogInterceptor"/>.
/// </summary>
/// <param name="serviceProvider"></param>
public partial class UserActivityLogInterceptor(IServiceProvider serviceProvider) : IMilvaInterceptor
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IMilvaionRepositoryBase<ActivityLog> _activityLogRepository = serviceProvider.GetService<IMilvaionRepositoryBase<ActivityLog>>();

    /// <inheritdoc/>
    public int InterceptionOrder { get; set; } = 999;

    /// <inheritdoc/>
    public async Task OnInvoke(Call call)
    {
        await call.NextAsync();

        if (call.ReturnType.CanAssignableTo(typeof(IResponse<>)))
        {
            var response = call.ReturnValue as IResponse;

            if (response.IsSuccess)
            {

                try
                {
                    var activityLogAttribute = call.GetInterceptorAttribute<UserActivityTrackAttribute>();

                    var entity = new ActivityLog
                    {
                        Activity = activityLogAttribute.Activity,
                        ActivityDate = DateTimeOffset.UtcNow,
                        UserName = User.GetCurrentUser(_serviceProvider),
                    };

                    await _activityLogRepository.AddAsync(entity);
                }
                catch (Exception)
                {
                    // Ignored because the log should not affect the main process.
                }
            }
        }
    }
}