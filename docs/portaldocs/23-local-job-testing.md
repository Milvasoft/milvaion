---
id: local-job-testing
title: Local Job Testing
sidebar_position: 23
description: Test your job implementations locally without RabbitMQ, Redis, or a running Milvaion environment.
---


# Local Job Testing

This guide explains how to unit-test your job implementations locally. The `Milvasoft.Milvaion.Sdk.Worker` package lets you execute any job and inspect its result without a running RabbitMQ, Redis, or database.

## Why Test Locally?

When you develop a new job in your worker, the typical deployment cycle looks like this:

```
Code → Build → Docker image → Deploy to Dev → Activate job → Trigger manually → Check logs
```

This cycle is slow and requires a live environment for every iteration. Local testing short-circuits this by running the exact same job execution path directly in a test process:

| | Local Test | Live Environment |
|---|---|---|
| **RabbitMQ** | Not required | Required |
| **Redis** | Not required | Required |
| **Database** | Not required | Required |
| **Milvaion API** | Not required | Required |
| **Feedback speed** | Milliseconds | Minutes |
| **Debugger** | Full support | Not available |
| **Result inspection** | Programmatic | Dashboard only |

> **Tip**: Local tests do not replace live testing in a Dev environment. Use them to iterate on business logic fast, then validate end-to-end in Dev before promoting.

---

## Template — Pre-configured Test Project

If you created your worker from the **Milvaion Worker Template**, a test project is already included. You don't need to create or configure anything.

The template generates the following structure out of the box:

```
MyWorker/
├── MyWorker.csproj          ← your worker
├── MyJobs.cs
├── Program.cs
└── MyWorker.Tests/
    ├── MyWorker.Tests.csproj  ← test project (pre-configured)
    └── SampleJobTests.cs      ← ready-to-run example tests
```

The test project already has:
- `xUnit`, `FluentAssertions`, and `Microsoft.NET.Test.Sdk` referenced
- A `ProjectReference` pointing to your worker
- Sample tests for every job type included in the template

To verify everything works right after scaffolding, just run:

```bash
dotnet test MyWorker.Tests
```

All sample tests should pass immediately with no additional setup.

> If you used the template, skip to [JobTestRunner API](#jobtestrunner-api) and start writing tests for your own jobs.

---

## Setup

### 1. Create a Test Project

Add an xUnit test project alongside your worker:

```bash
dotnet new xunit -n MyWorker.Tests
cd MyWorker.Tests
```

### 2. Add References

Add the testing SDK and a reference to your worker project:

```bash
dotnet add reference ../MyWorker/MyWorker.csproj
dotnet add package Milvasoft.Milvaion.Sdk.Worker
dotnet add package FluentAssertions
```

Your `.csproj` should look like:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
	<TargetFramework>net10.0</TargetFramework>
	<IsTestProject>true</IsTestProject>
	<Nullable>disable</Nullable>
  </PropertyGroup>

  <ItemGroup>
	<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.5.1" />
	<PackageReference Include="xunit" Version="2.9.3" />
	<PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
	<PackageReference Include="FluentAssertions" Version="7.0.0" />
	<PackageReference Include="Milvasoft.Milvaion.Sdk.Worker" Version="10.1.3" />
  </ItemGroup>

  <ItemGroup>
	<ProjectReference Include="..\MyWorker\MyWorker.csproj" />
  </ItemGroup>

</Project>
```

---

## JobTestRunner API

`JobTestRunner` is a fluent builder that wraps `JobExecutor` — the same component that runs your jobs in production.

### Builder Methods

| Method | Description | Default |
|--------|-------------|---------|
| `For(IJobBase job)` | Sets the job instance to run | — |
| `WithJobData<T>(T data)` | Serializes `data` as JSON and passes it as job data | `{}` |
| `WithJobData(string json)` | Sets raw JSON string as job data | `{}` |
| `WithTimeout(int seconds)` | Sets execution timeout; `0` disables timeout | `30` |
| `WithWorkerId(string id)` | Sets the worker ID in the execution context | `"local-test"` |
| `WithCancellationToken(CancellationToken)` | Passes a cancellation token | `None` |
| `WithLoggerFactory(ILoggerFactory)` | Captures logs through a custom logger | Console |

All methods return the builder, so they can be chained freely.

### Return Value: `JobExecutionResult`

`RunAsync()` returns a `JobExecutionResult` record with the following fields:

| Field | Type | Description |
|-------|------|-------------|
| `Status` | `JobOccurrenceStatus` | `Completed`, `Failed`, `Cancelled`, `TimedOut` |
| `DurationMs` | `long` | Wall-clock time of the job in milliseconds |
| `Exception` | `string` | Exception message and inner exception chain (if any) |
| `Result` | `string` | Serialized return value for `IJobWithResult<T>` jobs |
| `Logs` | `List<OccurrenceLog>` | All log entries written via `context.Log*()` |
| `IsPermanentFailure` | `bool` | `true` if a `PermanentJobException` was thrown |
| `CorrelationId` | `Guid` | Correlation ID of this test run |

---

## Basic Usage

### Simplest Test

```csharp
[Fact]
public async Task MyJob_ShouldComplete_WhenRunNormally()
{
	var result = await JobTestRunner
		.For(new MyJob())
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Completed);
	result.Exception.Should().BeNull();
}
```

### With Typed Job Data

When your job reads typed data via `context.GetData<T>()`, pass it with `WithJobData<T>()`:

```csharp
[Fact]
public async Task SendInvoiceJob_ShouldComplete_WhenDataIsValid()
{
	var jobData = new InvoiceJobData
	{
		CustomerId = 42,
		InvoiceId  = 1001,
		Currency   = "USD"
	};

	var result = await JobTestRunner
		.For(new SendInvoiceJob())
		.WithJobData(jobData)
		.WithTimeout(60)
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Completed);
	result.Exception.Should().BeNull();
	result.DurationMs.Should().BeGreaterThan(0);
}
```

### With Raw JSON

When you want full control over the raw payload:

```csharp
[Fact]
public async Task MyJob_ShouldHandleMissingFields_Gracefully()
{
	var result = await JobTestRunner
		.For(new MyJob())
		.WithJobData("""{"customerId": 99}""")
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Completed);
}
```

---

## Testing Failure Scenarios

### Exception Handling

```csharp
[Fact]
public async Task ProcessOrderJob_ShouldFail_WhenOrderNotFound()
{
	var result = await JobTestRunner
		.For(new ProcessOrderJob())
		.WithJobData(new OrderJobData { OrderId = -1 })
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Failed);
	result.Exception.Should().Contain("Order not found");
	result.IsPermanentFailure.Should().BeFalse();
}
```

### Permanent Failures

If your job throws `PermanentJobException`, the result marks the failure as permanent (no retry):

```csharp
// In your job
throw new PermanentJobException("Invalid invoice data — will not retry.");
```

```csharp
// In your test
[Fact]
public async Task ProcessOrderJob_ShouldFailPermanently_WhenDataIsInvalid()
{
	var result = await JobTestRunner
		.For(new ProcessOrderJob())
		.WithJobData(new OrderJobData { OrderId = 0 })
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Failed);
	result.IsPermanentFailure.Should().BeTrue();
}
```

---

## Testing Timeouts

Use `WithTimeout()` to verify your job respects execution time limits:

```csharp
[Fact]
public async Task LongRunningJob_ShouldTimeOut_WhenExceedsLimit()
{
	var result = await JobTestRunner
		.For(new DataSyncJob())
		.WithTimeout(1)  // 1 second — much shorter than the job's actual runtime
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
	result.Exception.Should().Contain("timeout");
}
```

> **Note**: The timeout in tests uses the same `CancellationTokenSource` mechanism as production. If your job passes `context.CancellationToken` to all async operations, it will be cancelled correctly.

---

## Testing Cancellation

Test that your job stops cleanly when cancelled:

```csharp
[Fact]
public async Task MyJob_ShouldCancel_WhenTokenCancelled()
{
	using var cts = new CancellationTokenSource();
	await cts.CancelAsync();

	var result = await JobTestRunner
		.For(new MyJob())
		.WithCancellationToken(cts.Token)
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
}

[Fact]
public async Task MyJob_ShouldCancelMidExecution_WhenTokenCancelledAfterDelay()
{
	using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

	var result = await JobTestRunner
		.For(new MyJob())
		.WithCancellationToken(cts.Token)
		.WithTimeout(0)  // Disable runner timeout; only use the external token
		.RunAsync();

	result.Status.Should().BeOneOf(JobOccurrenceStatus.Cancelled, JobOccurrenceStatus.Completed);
}
```

---

## Testing Jobs with Return Values

For jobs implementing `IAsyncJobWithResult<T>` or `IJobWithResult<T, TSchema>`, the serialized result is available in `result.Result`:

```csharp
[Fact]
public async Task GenerateReportJob_ShouldReturnFilePath_WhenSucceeds()
{
	var result = await JobTestRunner
		.For(new GenerateReportJob())
		.WithJobData(new ReportJobData { ReportType = "monthly", Month = 6 })
		.WithTimeout(120)
		.RunAsync();

	result.Status.Should().Be(JobOccurrenceStatus.Completed);
	result.Result.Should().NotBeNullOrEmpty();
	result.Result.Should().Contain("monthly");
}
```

---

## Testing Jobs with Dependency Injection

When your job has constructor-injected services, build the job manually using a `ServiceCollection`:

```csharp
[Fact]
public async Task SendInvoiceJob_ShouldSendEmail_WhenEmailServiceAvailable()
{
	// Arrange — wire up services
	var services = new ServiceCollection();
	services.AddLogging(b => b.AddConsole());
	services.AddScoped<IEmailService, SmtpEmailService>();  // or a stub/mock
	services.AddScoped<IInvoiceRepository, InvoiceRepository>();
	var sp = services.BuildServiceProvider();

	// Resolve the job from DI (same as production)
	var job = sp.GetRequiredService<SendInvoiceJob>();

	// Act
	var result = await JobTestRunner
		.For(job)
		.WithJobData(new InvoiceJobData { InvoiceId = 1001 })
		.RunAsync();

	// Assert
	result.Status.Should().Be(JobOccurrenceStatus.Completed);
}
```

### Using Mocks

For unit tests where you want to isolate the job from real services, use Moq:

```csharp
[Fact]
public async Task SendInvoiceJob_ShouldCallEmailService_ExactlyOnce()
{
	// Arrange
	var emailServiceMock = new Mock<IEmailService>();
	emailServiceMock
		.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
		.Returns(Task.CompletedTask);

	var job = new SendInvoiceJob(emailServiceMock.Object);

	// Act
	var result = await JobTestRunner
		.For(job)
		.WithJobData(new InvoiceJobData { InvoiceId = 1001, RecipientEmail = "client@example.com" })
		.RunAsync();

	// Assert
	result.Status.Should().Be(JobOccurrenceStatus.Completed);
	emailServiceMock.Verify(
		x => x.SendAsync("client@example.com", It.IsAny<string>(), It.IsAny<CancellationToken>()),
		Times.Once);
}
```

---

## Capturing Logs in Tests

By default `JobTestRunner` writes to the console. To capture log output in xUnit test output, supply a custom `ILoggerFactory`:

```csharp
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

public class MyJobTests(ITestOutputHelper output)
{
	private ILoggerFactory CreateLoggerFactory() =>
		LoggerFactory.Create(b =>
		{
			b.SetMinimumLevel(LogLevel.Debug);
			b.AddConsole();
			// Optional: pipe to xUnit output via a custom provider
		});

	[Fact]
	public async Task MyJob_ShouldLogStartAndCompletion()
	{
		var result = await JobTestRunner
			.For(new MyJob())
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		// Logs written via context.Log*() are available in result.Logs
		result.Logs.Should().Contain(l => l.Message.Contains("started"));
		result.Logs.Should().Contain(l => l.Message.Contains("completed"));
	}
}
```

> **Note**: `result.Logs` contains every entry written by `context.LogInformation()`, `context.LogWarning()`, and `context.LogError()` — the same entries that appear in the Milvaion dashboard occurrence detail view.

---

## Recommended Test Structure

Organize your test file to cover all meaningful scenarios for each job:

```
MyWorker.Tests/
├── MyWorker.Tests.csproj
└── Jobs/
	├── ProcessOrderJobTests.cs
	├── SendInvoiceJobTests.cs
	└── DataSyncJobTests.cs
```

A typical test class per job:

```csharp
public class ProcessOrderJobTests
{
	// ── Happy path ──────────────────────────────────────────
	[Fact]
	public async Task ShouldComplete_WhenOrderIsValid() { ... }

	// ── Job data edge cases ─────────────────────────────────
	[Fact]
	public async Task ShouldComplete_WhenJobDataIsNull() { ... }

	[Fact]
	public async Task ShouldFail_WhenOrderIdIsNegative() { ... }

	// ── Resilience ──────────────────────────────────────────
	[Fact]
	public async Task ShouldFailPermanently_WhenOrderIsDuplicate() { ... }

	[Fact]
	public async Task ShouldTimeOut_WhenExceedsTimeLimit() { ... }

	[Fact]
	public async Task ShouldCancel_WhenTokenCancelled() { ... }
}
```

---

## Complete Example

The `SampleWorker.Tests` project in the repository demonstrates the full pattern across multiple job types:

```csharp
public class SampleJobTests
{
	private ILoggerFactory CreateLoggerFactory() =>
		LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

	[Fact]
	public async Task TestJob_ShouldComplete_WhenRunNormally()
	{
		var result = await JobTestRunner
			.For(new TestJob())
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.Completed);
		result.Exception.Should().BeNull();
		result.DurationMs.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task SampleSendEmailJob_ShouldComplete_WhenValidDataProvided()
	{
		var jobData = new EmailJobData
		{
			To       = "test@example.com",
			Subject  = "Local test email",
			Body     = "Hello from local test!"
		};

		var result = await JobTestRunner
			.For(new SampleSendEmailJob())
			.WithJobData(jobData)
			.WithTimeout(120)
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.Completed);
	}

	[Fact]
	public async Task AlwaysFailingJob_ShouldFail_WithExceptionMessage()
	{
		var result = await JobTestRunner
			.For(new AlwaysFailingJob())
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.Failed);
		result.Exception.Should().Contain("always fails");
	}

	[Fact]
	public async Task HaveResultJob_ShouldReturnSerializedResult()
	{
		var result = await JobTestRunner
			.For(new HaveResultJob())
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.Completed);
		result.Result.Should().Contain("Test Product");
	}

	[Fact]
	public async Task TestJob_ShouldTimeOut_WhenTimeoutIsVeryShort()
	{
		var result = await JobTestRunner
			.For(new TestJob())
			.WithTimeout(1)
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
	}

	[Fact]
	public async Task TestJob_ShouldBeCancelled_WhenTokenCancelled()
	{
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		var result = await JobTestRunner
			.For(new TestJob())
			.WithCancellationToken(cts.Token)
			.WithLoggerFactory(CreateLoggerFactory())
			.RunAsync();

		result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
	}
}
```

---

## What's Not Tested

Local tests use `JobExecutor` directly and therefore do not cover:

| Concern | Where to Test |
|---------|--------------|
| RabbitMQ message routing | Integration tests / Dev environment |
| Retry + DLQ behavior | Integration tests / Dev environment |
| Concurrent execution limits | Dev environment |
| Heartbeat / zombie detection | Dev environment |
| Dashboard visibility | Dev environment |
| Redis cancellation channel | Integration tests |

---

## What's Next?

- **[Implementing Jobs](05-implementing-jobs.md)** - Advanced job patterns: DI, error handling, long-running jobs
- **[Configuration](06-configuration.md)** - Timeout and retry configuration for production
- **[Reliability](08-reliability.md)** - Retry, DLQ, and permanent failure handling
- **[Monitoring](10-monitoring.md)** - Viewing job logs and occurrence details in the dashboard
