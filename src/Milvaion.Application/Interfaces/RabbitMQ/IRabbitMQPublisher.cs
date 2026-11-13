using Milvaion.Application.Interfaces.Messaging;

namespace Milvaion.Application.Interfaces.RabbitMQ;

/// <summary>
/// RabbitMQ publisher for dispatching jobs to workers. Registered only when the transport is RabbitMQ;
/// depend on <see cref="IJobPublisher"/> to work with either transport.
/// </summary>
public interface IRabbitMQPublisher : IJobPublisher;
