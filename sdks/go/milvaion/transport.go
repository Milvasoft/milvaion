package milvaion

import (
	"context"
	"fmt"
)

// transport is the broker side of a worker: connecting, consuming jobs and handing them to the
// processor, and stopping. Publishing goes through the publisher the transport provides.
type transport interface {
	// start connects, registers the instance and starts consuming. It gives up when startCtx ends;
	// ctx is the worker's own context.
	start(ctx, startCtx context.Context) error
	// stopConsuming stops taking new jobs. Running jobs carry on and can still report their outcome.
	stopConsuming()
	// drained runs once running jobs have finished or have been returned to the broker.
	drained()
	// close disconnects after the final heartbeat and logs have been sent.
	close()
}

// rabbitTransport is the RabbitMQ transport: one queue per job type, reconnecting on its own.
type rabbitTransport struct {
	w              *Worker
	stopConnection context.CancelFunc
	loopDone       chan struct{}
}

func (t *rabbitTransport) start(ctx, startCtx context.Context) error {
	w := t.w
	connCtx, stopConnection := context.WithCancel(context.Background())
	ready := make(chan struct{})
	t.stopConnection = stopConnection
	t.loopDone = make(chan struct{})

	go func() {
		defer close(t.loopDone)
		w.connectionLoop(connCtx, ready)
	}()

	select {
	case <-ready:
		return nil
	case <-startCtx.Done():
		t.close()

		if ctx.Err() != nil {
			return ctx.Err()
		}

		w.lastConnectErrMu.Lock()
		last := w.lastConnectErr
		w.lastConnectErrMu.Unlock()

		return fmt.Errorf("milvaion: could not connect to RabbitMQ within %s: %w", w.opts.startupTimeout, last)
	}
}

func (t *rabbitTransport) stopConsuming() { t.w.cancelConsumers() }

// drained: messages of interrupted jobs were already nacked with requeue.
func (t *rabbitTransport) drained() {}

func (t *rabbitTransport) close() {
	if t.stopConnection != nil {
		t.stopConnection()
		<-t.loopDone
	}
}
