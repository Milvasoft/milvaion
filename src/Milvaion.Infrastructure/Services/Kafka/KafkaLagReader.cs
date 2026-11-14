using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Milvaion.Infrastructure.Services.Kafka;

/// <summary>
/// Consumer lag of one group on one topic.
/// </summary>
/// <param name="Topic">Topic name.</param>
/// <param name="Group">Consumer group that reads it.</param>
/// <param name="Lag">Records published but not committed by the group, summed over partitions.</param>
/// <param name="Consumers">Group members with at least one partition of the topic assigned, or null when unknown.</param>
public sealed record KafkaTopicLag(string Topic, string Group, long Lag, int? Consumers);

/// <summary>
/// Reads consumer lag with the admin API.
/// </summary>
public sealed class KafkaLagReader(KafkaClientFactory clientFactory)
{
    private readonly KafkaClientFactory _clientFactory = clientFactory;

    /// <summary>
    /// Lag of <paramref name="group"/> on <paramref name="topic"/>. A partition the group has never committed counts
    /// from its earliest offset, since the group reads from the beginning. Returns null when the topic does not exist.
    /// </summary>
    public async Task<KafkaTopicLag> GetLagAsync(string topic, string group, CancellationToken cancellationToken = default)
    {
        var metadata = _clientFactory.Admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var topicMetadata = metadata.Topics.FirstOrDefault(t => t.Topic == topic);

        if (topicMetadata == null || topicMetadata.Error.Code != ErrorCode.NoError || topicMetadata.Partitions.Count == 0)
            return null;

        var partitions = topicMetadata.Partitions.Select(p => new TopicPartition(topic, p.PartitionId)).ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var latest = await ListOffsetsAsync(partitions, OffsetSpec.Latest());
        var earliest = await ListOffsetsAsync(partitions, OffsetSpec.Earliest());

        var committed = new Dictionary<TopicPartition, long>();

        try
        {
            var groupOffsets = await _clientFactory.Admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, partitions)]);

            foreach (var partition in groupOffsets.SelectMany(g => g.Partitions))
            {
                if (partition.Offset.Value >= 0)
                    committed[partition.TopicPartition] = partition.Offset.Value;
            }
        }
        catch (ListConsumerGroupOffsetsException)
        {
            // The group does not exist yet: nothing committed.
        }

        long lag = 0;

        foreach (var partition in partitions)
        {
            var high = latest.GetValueOrDefault(partition);
            var from = committed.TryGetValue(partition, out var offset) ? offset : earliest.GetValueOrDefault(partition);

            lag += Math.Max(0, high - from);
        }

        return new KafkaTopicLag(topic, group, lag, await CountConsumersAsync(topic, group));
    }

    private async Task<Dictionary<TopicPartition, long>> ListOffsetsAsync(List<TopicPartition> partitions, OffsetSpec spec)
    {
        // A topic created a moment ago may not have partition leaders yet; that clears up within a second or two.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _clientFactory.Admin.ListOffsetsAsync(partitions.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = spec }));

                return result.ResultInfos.ToDictionary(r => r.TopicPartitionOffsetError.TopicPartition, r => r.TopicPartitionOffsetError.Offset.Value);
            }
            catch (ListOffsetsException) when (attempt < 3)
            {
                await Task.Delay(500 * attempt);
            }
        }
    }

    private async Task<int?> CountConsumersAsync(string topic, string group)
    {
        try
        {
            var description = await _clientFactory.Admin.DescribeConsumerGroupsAsync([group]);

            return description.ConsumerGroupDescriptions
                              .SelectMany(g => g.Members)
                              .Count(m => m.Assignment?.TopicPartitions?.Any(tp => tp.Topic == topic) == true);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
