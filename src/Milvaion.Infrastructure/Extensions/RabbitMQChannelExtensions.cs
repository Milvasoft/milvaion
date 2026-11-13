using Milvasoft.Core.Abstractions;
using RabbitMQ.Client;

namespace Milvaion.Infrastructure.Extensions;

/// <summary>
/// Extension methods for RabbitMQ IChannel operations.
/// Provides thread-safe ACK/NACK operations with proper error handling.
/// </summary>
public static class RabbitMQChannelExtensions
{
    /// <summary>
    /// Safe ACK operation that checks channel state before acknowledgment.
    /// Uses provided lock for thread-safety as IChannel is NOT thread-safe.
    /// Prevents "Already closed" exceptions during shutdown.
    /// </summary>
    /// <param name="channel">The RabbitMQ channel.</param>
    /// <param name="deliveryTag">The delivery tag to acknowledge.</param>
    /// <param name="channelLock">Semaphore for thread-safe channel access.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="multiple">If true, ACKs all messages up to and including deliveryTag.</param>
    public static async Task SafeAckAsync(this IChannel channel,
                                          ulong deliveryTag,
                                          SemaphoreSlim channelLock,
                                          IMilvaLogger logger,
                                          CancellationToken cancellationToken,
                                          bool multiple = false)
    {
        if (cancellationToken.IsCancellationRequested || channel == null || channel.IsClosed || !await TryEnterAsync(channelLock, cancellationToken))
        {
            logger?.Debug("Skipping ACK: Channel closed or shutdown requested (DeliveryTag: {DeliveryTag})", deliveryTag);
            return;
        }

        try
        {
            if (channel == null || channel.IsClosed)
            {
                logger?.Debug("Skipping ACK: Channel closed after acquiring lock (DeliveryTag: {DeliveryTag})", deliveryTag);
                return;
            }

            await channel.BasicAckAsync(deliveryTag, multiple, cancellationToken);
        }
        catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
            logger?.Debug("Channel already closed during ACK (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - don't log as warning
            logger?.Debug("ACK cancelled (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Failed to ACK message (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        finally
        {
            Exit(channelLock);
        }
    }

    /// <summary>
    /// Safe NACK operation that checks channel state before negative acknowledgment.
    /// Uses provided lock for thread-safety as IChannel is NOT thread-safe.
    /// Prevents "Already closed" exceptions during shutdown.
    /// </summary>
    /// <param name="channel">The RabbitMQ channel.</param>
    /// <param name="deliveryTag">The delivery tag to negative acknowledge.</param>
    /// <param name="channelLock">Semaphore for thread-safe channel access.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="multiple">If true, NACKs all messages up to and including deliveryTag.</param>
    /// <param name="requeue">If true, requeues the message(s).</param>
    public static async Task SafeNackAsync(this IChannel channel,
                                           ulong deliveryTag,
                                           SemaphoreSlim channelLock,
                                           IMilvaLogger logger,
                                           CancellationToken cancellationToken,
                                           bool multiple = false,
                                           bool requeue = false)
    {
        if (cancellationToken.IsCancellationRequested || channel == null || channel.IsClosed || !await TryEnterAsync(channelLock, cancellationToken))
        {
            logger?.Debug("Skipping NACK: Channel closed or shutdown requested (DeliveryTag: {DeliveryTag})", deliveryTag);
            return;
        }

        try
        {
            if (channel == null || channel.IsClosed)
            {
                logger?.Debug("Skipping NACK: Channel closed after acquiring lock (DeliveryTag: {DeliveryTag})", deliveryTag);
                return;
            }

            await channel.BasicNackAsync(deliveryTag, multiple, requeue, cancellationToken);
        }
        catch (RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
            logger?.Debug("Channel already closed during NACK (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - don't log as warning
            logger?.Debug("NACK cancelled (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Failed to NACK message (DeliveryTag: {DeliveryTag})", deliveryTag);
        }
        finally
        {
            Exit(channelLock);
        }
    }

    // A consumer callback can outlive the service's semaphores during shutdown.
    private static async Task<bool> TryEnterAsync(SemaphoreSlim channelLock, CancellationToken cancellationToken)
    {
        try
        {
            await channelLock.WaitAsync(cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    private static void Exit(SemaphoreSlim channelLock)
    {
        try
        {
            channelLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already released everything.
        }
    }

    /// <summary>
    /// Postgres data exceptions (class 22) and integrity violations (class 23), and payloads that cannot be
    /// converted at all, fail the same way on every retry. Requeueing such a message would block its queue forever.
    /// </summary>
    public static bool IsPermanentDataError(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException pgEx && (pgEx.SqlState.StartsWith("22") || pgEx.SqlState.StartsWith("23")))
                return true;

            if (current is System.Text.Json.JsonException or InvalidCastException or FormatException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Completes when the channel closes, for whatever reason. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="stoppingToken"/> is cancelled first. A consumer waits on this so that a dead channel leads to a reconnect.
    /// </summary>
    public static async Task WaitForShutdownAsync(this IChannel channel, CancellationToken stoppingToken)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        // The channel may have closed before the handler above was attached.
        if (channel.IsClosed)
            closed.TrySetResult();

        await closed.Task.WaitAsync(stoppingToken);
    }

    /// <summary>
    /// Runs a consumer until the application stops, reconnecting whenever the channel dies or the connection attempt fails.
    /// A consumer that gives up would stop processing silently while the process still looks healthy, so this never stops retrying.
    /// </summary>
    /// <param name="connectAndConsume">Connects and consumes. Returns when the channel has died.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="serviceName">Name used in logs.</param>
    /// <param name="onDegraded">Called once per outage, after <paramref name="alertAfterAttempts"/> consecutive failures.</param>
    /// <param name="stoppingToken">Stops the loop.</param>
    /// <param name="alertAfterAttempts">Consecutive failures before <paramref name="onDegraded"/> is called.</param>
    public static async Task RunConsumerLoopAsync(Func<CancellationToken, Task> connectAndConsume,
                                                  IMilvaLogger logger,
                                                  string serviceName,
                                                  Action<int> onDegraded,
                                                  CancellationToken stoppingToken,
                                                  int alertAfterAttempts = 10)
    {
        const int baseDelaySeconds = 5;
        const int maxDelaySeconds = 60;

        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                await connectAndConsume(stoppingToken);

                // Connected, consumed, and then lost the channel: a new outage, reconnect quickly.
                failures = 0;
                delay = TimeSpan.FromSeconds(1);

                logger.Warning("{Service} lost its message broker consumer. Reconnecting...", serviceName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.Information("{Service} is shutting down", serviceName);

                return;
            }
            catch (Exception ex)
            {
                failures++;

                logger.Error(ex, "{Service} message broker connection failed (consecutive failures: {Failures})", serviceName, failures);

                if (failures == alertAfterAttempts)
                {
                    logger.Fatal("{Service} failed to connect after {Failures} attempts and is not processing messages. Retrying until it recovers.", serviceName, failures);

                    onDegraded?.Invoke(failures);
                }

                delay = TimeSpan.FromSeconds(Math.Min(baseDelaySeconds * failures, maxDelaySeconds));
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Safely closes and disposes the channel with proper error handling.
    /// </summary>
    /// <param name="channel">The RabbitMQ channel.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task SafeCloseAsync(this IChannel channel,
                                            IMilvaLogger logger,
                                            CancellationToken cancellationToken)
    {
        if (channel == null)
            return;

        try
        {
            if (!channel.IsClosed)
            {
                await channel.CloseAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Error closing RabbitMQ channel");
        }
        finally
        {
            channel.Dispose();
        }
    }

    /// <summary>
    /// Safely closes and disposes the connection with proper error handling.
    /// </summary>
    /// <param name="connection">The RabbitMQ connection.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task SafeCloseAsync(this IConnection connection,
                                            IMilvaLogger logger,
                                            CancellationToken cancellationToken)
    {
        if (connection == null)
            return;

        try
        {
            if (connection.IsOpen)
            {
                await connection.CloseAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Error closing RabbitMQ connection");
        }
        finally
        {
            connection.Dispose();
        }
    }
}
