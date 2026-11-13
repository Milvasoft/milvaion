package milvaion

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"

	"github.com/redis/go-redis/v9"
)

// cancellationListener receives the cancellation requests the API publishes on Redis.
type cancellationListener struct {
	client  *redis.Client
	pubsub  *redis.PubSub
	channel string
	cancels *cancelRegistry
	logger  *slog.Logger
}

func startCancellationListener(ctx context.Context, url, channel string, cancels *cancelRegistry, logger *slog.Logger) (*cancellationListener, error) {
	options, err := redis.ParseURL(url)

	if err != nil {
		return nil, fmt.Errorf("milvaion: invalid RedisURL: %w", err)
	}

	client := redis.NewClient(options)

	if err := client.Ping(ctx).Err(); err != nil {
		_ = client.Close()
		return nil, fmt.Errorf("milvaion: could not connect to Redis: %w", err)
	}

	pubsub := client.Subscribe(ctx, channel)

	// Confirms the subscription before jobs start; go-redis resubscribes on its own after a reconnect.
	if _, err := pubsub.Receive(ctx); err != nil {
		_ = pubsub.Close()
		_ = client.Close()
		return nil, fmt.Errorf("milvaion: could not subscribe to %s: %w", channel, err)
	}

	l := &cancellationListener{client: client, pubsub: pubsub, channel: channel, cancels: cancels, logger: logger}

	go l.listen()
	logger.Info("listening for cancellations", "channel", channel)

	return l, nil
}

func (l *cancellationListener) listen() {
	for message := range l.pubsub.Channel() {
		var request cancellationRequest

		if err := json.Unmarshal([]byte(message.Payload), &request); err != nil {
			l.logger.Debug("ignored a cancellation message that is not JSON")
			continue
		}

		occurrenceID := request.CorrelationID

		if occurrenceID == "" {
			occurrenceID = request.OccurrenceID
		}

		if occurrenceID != "" && l.cancels.cancel(occurrenceID, request.Reason) {
			l.logger.Info("cancelling occurrence", "occurrence", occurrenceID, "reason", request.Reason)
		}
	}
}

func (l *cancellationListener) close() {
	_ = l.pubsub.Close()
	_ = l.client.Close()
}
