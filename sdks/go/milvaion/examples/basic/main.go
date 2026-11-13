// Command basic is a sample Milvaion worker.
//
//	MILVAION_WORKER_ID=go-sample-worker \
//	MILVAION_RABBITMQ_URL=amqp://guest:guest@localhost:5672 \
//	MILVAION_REDIS_URL=redis://localhost:6379 \
//	go run ./examples/basic
package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/Milvasoft/milvaion/sdks/go/milvaion"
)

type greetData struct {
	Name    string `json:"name"`
	DelayMs int    `json:"delayMs"`
}

func main() {
	options, err := milvaion.OptionsFromEnv(milvaion.Options{HeartbeatInterval: 5 * time.Second})

	if err != nil {
		log.Fatal(err)
	}

	if options.WorkerID == "" {
		options.WorkerID = "go-sample-worker"
	}

	worker, err := milvaion.NewWorker(options)

	if err != nil {
		log.Fatal(err)
	}

	must(worker.Handle("GreetJob", func(ctx context.Context, job *milvaion.Job) (any, error) {
		data := greetData{DelayMs: 1000}

		if err := job.Bind(&data); err != nil {
			return nil, err
		}

		if data.Name == "" {
			return nil, milvaion.Permanentf(`job data needs a "name"`)
		}

		job.Log("Greeting "+data.Name, map[string]any{"attempt": job.RetryCount + 1})

		// Honour ctx so cancelling from the dashboard stops the wait.
		select {
		case <-time.After(time.Duration(data.DelayMs) * time.Millisecond):
		case <-ctx.Done():
			return nil, context.Cause(ctx)
		}

		return map[string]string{"greeting": fmt.Sprintf("Hello, %s!", data.Name)}, nil
	}, milvaion.JobOptions{
		ExecutionTimeout: time.Minute,
		DataSchema: map[string]any{
			"type": "object",
			"properties": map[string]any{
				"name":    map[string]any{"type": "string", "description": "Who to greet"},
				"delayMs": map[string]any{"type": "integer", "description": "How long the job takes"},
			},
			"required": []string{"name"},
		},
	}))

	must(worker.Handle("FlakyJob", func(_ context.Context, job *milvaion.Job) (any, error) {
		// Fails on the first attempt to show retries in the dashboard.
		if job.RetryCount == 0 {
			return nil, errors.New("simulated transient failure")
		}

		job.Log("Succeeded on retry", nil)

		return nil, nil
	}, milvaion.JobOptions{MaxRetries: 2, BaseRetryDelay: 2 * time.Second}))

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if err := worker.Run(ctx); err != nil {
		log.Fatal(err)
	}
}

func must(err error) {
	if err != nil {
		log.Fatal(err)
	}
}
