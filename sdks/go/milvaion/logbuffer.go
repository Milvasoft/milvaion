package milvaion

import (
	"context"
	"log/slog"
	"sync"
	"time"
)

// logBuffer batches occurrence logs. A batch that fails to send is kept and sent first next time,
// so logs keep their order.
type logBuffer struct {
	send          func(ctx context.Context, batch workerLogBatchMessage) error
	logger        *slog.Logger
	batchSize     int
	flushInterval time.Duration
	maxBuffered   int

	mu      sync.Mutex
	buffer  []workerLogMessage
	timer   *time.Timer
	dropped int
	closed  bool

	flushMu sync.Mutex
	unsent  []workerLogMessage
}

func newLogBuffer(send func(context.Context, workerLogBatchMessage) error, logger *slog.Logger) *logBuffer {
	return &logBuffer{send: send, logger: logger, batchSize: 50, flushInterval: 500 * time.Millisecond, maxBuffered: 10_000}
}

func (b *logBuffer) add(occurrenceID, instanceID string, log occurrenceLog) {
	b.mu.Lock()

	if len(b.buffer) >= b.maxBuffered {
		b.buffer = b.buffer[1:]
		b.dropped++

		if b.dropped%1000 == 1 {
			b.logger.Warn("log buffer is full; dropping the oldest logs", "dropped", b.dropped)
		}
	}

	b.buffer = append(b.buffer, workerLogMessage{OccurrenceID: occurrenceID, WorkerID: instanceID, Log: log, MessageTimestamp: now()})
	full := len(b.buffer) >= b.batchSize

	if !full && b.timer == nil && !b.closed {
		b.timer = time.AfterFunc(b.flushInterval, b.onTimer)
	}

	b.mu.Unlock()

	if full {
		go func() { _ = b.flush(context.Background()) }()
	}
}

func (b *logBuffer) onTimer() {
	b.mu.Lock()
	b.timer = nil
	b.mu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	if err := b.flush(ctx); err != nil {
		b.logger.Debug("failed to send logs; will retry", "error", err)

		b.mu.Lock()

		if b.timer == nil && !b.closed {
			b.timer = time.AfterFunc(b.flushInterval, b.onTimer)
		}

		b.mu.Unlock()
	}
}

// flush sends everything buffered so far. On error the unsent batch stays buffered.
func (b *logBuffer) flush(ctx context.Context) error {
	b.flushMu.Lock()
	defer b.flushMu.Unlock()

	b.mu.Lock()
	// Bounded by what is buffered now, so producers that keep logging cannot keep this looping.
	perRound := b.batchSize * 2
	rounds := (len(b.buffer)+len(b.unsent)+perRound-1)/perRound + 1
	b.mu.Unlock()

	for ; rounds > 0; rounds-- {
		batch := b.unsent

		if batch == nil {
			b.mu.Lock()
			n := min(len(b.buffer), b.batchSize*2)
			batch = append([]workerLogMessage(nil), b.buffer[:n]...)
			b.buffer = b.buffer[n:]
			b.mu.Unlock()
		}

		if len(batch) == 0 {
			return nil
		}

		b.unsent = batch

		if err := b.send(ctx, workerLogBatchMessage{Logs: batch, BatchTimestamp: now(), Count: len(batch)}); err != nil {
			return err
		}

		b.unsent = nil
	}

	return nil
}

func (b *logBuffer) close() {
	b.mu.Lock()
	defer b.mu.Unlock()

	b.closed = true

	if b.timer != nil {
		b.timer.Stop()
		b.timer = nil
	}
}
