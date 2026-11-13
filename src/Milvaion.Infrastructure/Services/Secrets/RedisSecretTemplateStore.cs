using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Redis-backed <see cref="ISecretTemplateStore"/>. Only templates are stored here, never resolved values.
/// </summary>
public class RedisSecretTemplateStore(IConnectionMultiplexer redis,
                                      IOptions<RedisOptions> redisOptions,
                                      IOptionsMonitor<SecretsOptions> secretsOptions,
                                      ILoggerFactory loggerFactory) : ISecretTemplateStore
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RedisSecretTemplateStore>();

    /// <inheritdoc/>
    public async Task SaveAsync(Guid occurrenceId, string template, CancellationToken cancellationToken = default)
    {
        try
        {
            var hours = Math.Max(1, secretsOptions.CurrentValue.TemplateRetentionHours);

            await redis.GetDatabase().StringSetAsync(Key(occurrenceId), template, TimeSpan.FromHours(hours));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not save the job data template of occurrence {OccurrenceId}", occurrenceId);
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(occurrenceId));

            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not read the job data template of occurrence {OccurrenceId}", occurrenceId);

            return null;
        }
    }

    private string Key(Guid occurrenceId) => $"{redisOptions.Value.KeyPrefix}secret_template:{occurrenceId}";
}
