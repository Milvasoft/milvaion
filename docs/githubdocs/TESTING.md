# Testing Guide

This guide covers testing strategies and best practices for Milvaion development.

## Table of Contents

- [Overview](#overview)
- [Test Structure](#test-structure)
- [Unit Testing](#unit-testing)
- [Integration Testing](#integration-testing)
- [Testing Jobs](#testing-jobs)
- [Testing Background Services](#testing-background-services)
- [Test Data Management](#test-data-management)
- [Running Tests](#running-tests)
- [Test Coverage](#test-coverage)
- [Best Practices](#best-practices)

---

## Overview

Milvaion uses a comprehensive testing strategy:

| Test Type | Purpose | Tools | Speed |
|-----------|---------|-------|-------|
| **Unit Tests** | Test individual components in isolation | xUnit, Moq | Fast (~ms) |
| **Integration Tests** | Test component interactions with real dependencies | xUnit, Testcontainers | Slow (~seconds) |
| **End-to-End Tests** | Test complete workflows | Manual / Automated | Slowest (~minutes) |

### Testing Philosophy

- **Test behavior, not implementation**
- **Write tests first when fixing bugs**
- **Keep tests simple and readable**
- **Use meaningful test names**
- **One assertion concept per test**

---

## Test Structure

```
tests/
├── Milvaion.UnitTests/
│   ├── ComponentTests/           # Entity, Enum, JsonModel tests
│   │   └── DtoTests/             # DTO validation tests
│   ├── InfrastructureTests/      # Infrastructure service tests
│   ├── SdkTests/                 # Client SDK tests
│   ├── UtilsTests/               # Utility and helper tests
│   └── WorkerSdkTests/           # Worker SDK tests
│
└── Milvaion.IntegrationTests/
    ├── ControllersTests/         # API controller integration tests
    ├── BackgroundServices/       # Background service tests
    ├── WorkerSdk/                # Worker SDK integration tests
    └── TestBase/                 # Test base classes and utilities
```

### Naming Conventions

**Test Classes:**
```csharp
{ClassUnderTest}Tests
Example: CreateJobCommandHandlerTests
```

**Test Methods:**
```csharp
{MethodUnderTest}_{Scenario}_{ExpectedResult}
Example: Handle_ValidCommand_CreatesJob
```

---

## Unit Testing

Unit tests focus on testing individual components in isolation using mocks/stubs.

### Testing Command Handlers

```csharp
using FluentAssertions;
using Moq;
using Xunit;

public class CreateJobCommandHandlerTests
{
    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly Mock<IRedisScheduler> _schedulerMock;
    private readonly CreateJobCommandHandler _handler;

    public CreateJobCommandHandlerTests()
    {
        _repositoryMock = new Mock<IJobRepository>();
        _schedulerMock = new Mock<IRedisScheduler>();
        _handler = new CreateJobCommandHandler(
            _repositoryMock.Object,
            _schedulerMock.Object
        );
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesJobAndSchedules()
    {
        // Arrange
        var command = new CreateJobCommand(new CreateJobDto
        {
            DisplayName = "Test Job",
            WorkerId = "test-worker",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 * * *",
            IsActive = true
        });

        _repositoryMock
            .Setup(x => x.AddAsync(It.IsAny<ScheduledJob>()))
            .ReturnsAsync((ScheduledJob job) => job);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();

        _repositoryMock.Verify(
            x => x.AddAsync(It.Is<ScheduledJob>(j => j.DisplayName == "Test Job")),
            Times.Once
        );

        _schedulerMock.Verify(
            x => x.ScheduleAsync(It.IsAny<Guid>(), It.IsAny<DateTime>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Handle_InvalidCronExpression_ReturnsError()
    {
        // Arrange
        var command = new CreateJobCommand(new CreateJobDto
        {
            DisplayName = "Invalid Job",
            WorkerId = "test-worker",
            SelectedJobName = "TestJob",
            CronExpression = "invalid cron", // Invalid
            IsActive = true
        });

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("cron"));

        _repositoryMock.Verify(
            x => x.AddAsync(It.IsAny<ScheduledJob>()),
            Times.Never
        );
    }
}
```

### Testing Query Handlers

```csharp
public class GetJobListQueryHandlerTests
{
    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly GetJobListQueryHandler _handler;

    public GetJobListQueryHandlerTests()
    {
        _repositoryMock = new Mock<IJobRepository>();
        _handler = new GetJobListQueryHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ReturnsPagedJobs()
    {
        // Arrange
        var query = new GetJobListQuery(new ListRequest
        {
            PageIndex = 1,
            RequestedItemCount = 10
        });

        var jobs = new List<ScheduledJob>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "Job 1" },
            new() { Id = Guid.NewGuid(), DisplayName = "Job 2" }
        };

        _repositoryMock
            .Setup(x => x.GetListAsync(It.IsAny<ListRequest>()))
            .ReturnsAsync(new ListResponse<ScheduledJob>
            {
                DtoList = jobs,
                TotalDataCount = 2
            });

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.DtoList.Should().HaveCount(2);
        result.Data.TotalDataCount.Should().Be(2);
    }
}
```

### Testing Validators

```csharp
public class CreateJobDtoValidatorTests
{
    private readonly CreateJobDtoValidator _validator;

    public CreateJobDtoValidatorTests()
    {
        _validator = new CreateJobDtoValidator();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyDisplayName_HasError(string displayName)
    {
        // Arrange
        var dto = new CreateJobDto
        {
            DisplayName = displayName,
            WorkerId = "test-worker",
            SelectedJobName = "TestJob"
        };

        // Act
        var result = _validator.Validate(dto);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(dto.DisplayName));
    }

    [Fact]
    public void Validate_ValidDto_NoErrors()
    {
        // Arrange
        var dto = new CreateJobDto
        {
            DisplayName = "Valid Job",
            WorkerId = "test-worker",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 * * *"
        };

        // Act
        var result = _validator.Validate(dto);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }
}
```

### Testing Domain Entities

```csharp
public class ScheduledJobTests
{
    [Fact]
    public void UpdateNextExecutionTime_CronExpression_CalculatesCorrectly()
    {
        // Arrange
        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            CronExpression = "0 0 * * *", // Daily at midnight
            IsActive = true
        };

        var now = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        // Act
        job.UpdateNextExecutionTime(now);

        // Assert
        job.NextExecutionTime.Should().Be(new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Disable_SetsIsActiveToFalse()
    {
        // Arrange
        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            IsActive = true
        };

        // Act
        job.Disable();

        // Assert
        job.IsActive.Should().BeFalse();
    }
}
```

---

## Integration Testing

Integration tests use real dependencies (database, Redis, RabbitMQ) via Testcontainers.

### Setup with Testcontainers

```csharp
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.RabbitMq;

public class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer;
    private readonly RedisContainer _redisContainer;
    private readonly RabbitMqContainer _rabbitMqContainer;

    public IntegrationTestFixture()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("MilvaionTestDb")
            .WithUsername("postgres")
            .WithPassword("postgres123")
            .Build();

        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithUsername("guest")
            .WithPassword("guest")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        await _redisContainer.StartAsync();
        await _rabbitMqContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
        await _rabbitMqContainer.DisposeAsync();
    }

    public string GetPostgresConnectionString() => _postgresContainer.GetConnectionString();
    public string GetRedisConnectionString() => _redisContainer.GetConnectionString();
    public string GetRabbitMqConnectionString() => _rabbitMqContainer.GetConnectionString();
}
```

### Testing API Controllers

```csharp
public class JobsControllerTests : IClassFixture<WebApplicationFactory<Program>>, IClassFixture<IntegrationTestFixture>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IntegrationTestFixture _fixture;

    public JobsControllerTests(
        WebApplicationFactory<Program> factory,
        IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["ConnectionStrings:DefaultConnectionString"] = _fixture.GetPostgresConnectionString(),
                    ["MilvaionConfig:Redis:ConnectionString"] = _fixture.GetRedisConnectionString(),
                    ["MilvaionConfig:RabbitMQ:Host"] = _fixture.GetRabbitMqConnectionString()
                });
            });
        });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task CreateJob_ValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new CreateJobDto
        {
            DisplayName = "Integration Test Job",
            WorkerId = "test-worker",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 * * *",
            IsActive = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/jobs/job", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<Response<JobDto>>();
        result.IsSuccess.Should().BeTrue();
        result.Data.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetJobs_ReturnsPagedList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/jobs?pageIndex=1&requestedItemCount=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<Response<ListResponse<JobDto>>>();
        result.IsSuccess.Should().BeTrue();
        result.Data.DtoList.Should().NotBeNull();
    }
}
```

### Testing Repository

```csharp
public class JobRepositoryTests : IClassFixture<IntegrationTestFixture>
{
    private readonly MilvaionDbContext _dbContext;
    private readonly JobRepository _repository;

    public JobRepositoryTests(IntegrationTestFixture fixture)
    {
        var options = new DbContextOptionsBuilder<MilvaionDbContext>()
            .UseNpgsql(fixture.GetPostgresConnectionString())
            .Options;

        _dbContext = new MilvaionDbContext(options);
        _dbContext.Database.EnsureCreated();

        _repository = new JobRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_ValidJob_SavesToDatabase()
    {
        // Arrange
        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Job",
            WorkerId = "test-worker",
            JobType = "TestJob",
            IsActive = true,
            CreationDate = DateTime.UtcNow
        };

        // Act
        var result = await _repository.AddAsync(job);
        await _dbContext.SaveChangesAsync();

        // Assert
        var saved = await _dbContext.ScheduledJobs.FindAsync(job.Id);
        saved.Should().NotBeNull();
        saved.DisplayName.Should().Be("Test Job");
    }
}
```

---

## Testing Jobs

### Testing IAsyncJob Implementations

```csharp
public class SendEmailJobTests
{
    private readonly Mock<IEmailService> _emailServiceMock;
    private readonly Mock<ILogger<SendEmailJob>> _loggerMock;
    private readonly SendEmailJob _job;

    public SendEmailJobTests()
    {
        _emailServiceMock = new Mock<IEmailService>();
        _loggerMock = new Mock<ILogger<SendEmailJob>>();
        _job = new SendEmailJob(_emailServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ValidData_SendsEmail()
    {
        // Arrange
        var contextMock = CreateJobContext(new
        {
            to = "test@example.com",
            subject = "Test",
            body = "Test body"
        });

        _emailServiceMock
            .Setup(x => x.SendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _job.ExecuteAsync(contextMock.Object);

        // Assert
        _emailServiceMock.Verify(
            x => x.SendAsync(
                "test@example.com",
                "Test",
                "Test body",
                It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ExecuteAsync_EmailServiceFails_ThrowsException()
    {
        // Arrange
        var contextMock = CreateJobContext(new { to = "test@example.com" });

        _emailServiceMock
            .Setup(x => x.SendAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SmtpException("SMTP server error"));

        // Act & Assert
        await Assert.ThrowsAsync<SmtpException>(() => _job.ExecuteAsync(contextMock.Object));
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_StopsExecution()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var contextMock = CreateJobContext(new { to = "test@example.com" }, cts.Token);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _job.ExecuteAsync(contextMock.Object)
        );
    }

    private Mock<IJobContext> CreateJobContext(object jobData, CancellationToken cancellationToken = default)
    {
        var contextMock = new Mock<IJobContext>();

        contextMock.Setup(x => x.Job).Returns(new JobInfo
        {
            JobId = Guid.NewGuid(),
            JobType = "SendEmailJob",
            JobData = JsonSerializer.Serialize(jobData),
            Version = 1
        });

        contextMock.Setup(x => x.Occurrence).Returns(new OccurrenceInfo
        {
            OccurrenceId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            RetryCount = 0,
            MaxRetries = 3
        });

        contextMock.Setup(x => x.CancellationToken).Returns(cancellationToken);

        return contextMock;
    }
}
```

---

## Testing Background Services

```csharp
public class JobDispatcherServiceTests
{
    private readonly Mock<IRedisScheduler> _schedulerMock;
    private readonly Mock<IJobPublisher> _publisherMock;
    private readonly JobDispatcherService _service;

    public JobDispatcherServiceTests()
    {
        _schedulerMock = new Mock<IRedisScheduler>();
        _publisherMock = new Mock<IJobPublisher>();
        _service = new JobDispatcherService(
            _schedulerMock.Object,
            _publisherMock.Object
        );
    }

    [Fact]
    public async Task ExecuteAsync_DueJobs_PublishesToQueue()
    {
        // Arrange
        var dueJob = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "Due Job",
            WorkerId = "test-worker",
            JobType = "TestJob"
        };

        _schedulerMock
            .Setup(x => x.GetDueJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledJob> { dueJob });

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        // Act
        await _service.StartAsync(cts.Token);
        await Task.Delay(1500); // Let it process
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _publisherMock.Verify(
            x => x.PublishAsync(It.Is<JobMessage>(m => m.JobId == dueJob.Id)),
            Times.AtLeastOnce
        );
    }
}
```

---

## Test Data Management

### Test Data Builders

```csharp
public class JobBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _displayName = "Test Job";
    private string _workerId = "test-worker";
    private string _jobType = "TestJob";
    private string? _cronExpression = "0 0 * * *";
    private bool _isActive = true;

    public JobBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    public JobBuilder WithDisplayName(string displayName)
    {
        _displayName = displayName;
        return this;
    }

    public JobBuilder Inactive()
    {
        _isActive = false;
        return this;
    }

    public ScheduledJob Build()
    {
        return new ScheduledJob
        {
            Id = _id,
            DisplayName = _displayName,
            WorkerId = _workerId,
            JobType = _jobType,
            CronExpression = _cronExpression,
            IsActive = _isActive,
            CreationDate = DateTime.UtcNow
        };
    }
}

// Usage
var job = new JobBuilder()
    .WithDisplayName("My Test Job")
    .Inactive()
    .Build();
```

### Fixture Classes

```csharp
public class JobFixture
{
    public ScheduledJob CreateValidJob(string? displayName = null)
    {
        return new ScheduledJob
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName ?? "Test Job",
            WorkerId = "test-worker",
            JobType = "TestJob",
            CronExpression = "0 0 * * *",
            IsActive = true,
            CreationDate = DateTime.UtcNow
        };
    }

    public JobOccurrence CreateOccurrence(Guid jobId, OccurrenceStatus status = OccurrenceStatus.Completed)
    {
        return new JobOccurrence
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            Status = status,
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow,
            DurationMs = 5000,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
    }
}
```

---

## Running Tests

### Command Line

```bash
# Run all tests
dotnet test

# Run unit tests only
dotnet test tests/Milvaion.UnitTests

# Run integration tests
dotnet test tests/Milvaion.IntegrationTests

# Run specific test
dotnet test --filter "FullyQualifiedName~CreateJobCommandHandlerTests.Handle_ValidCommand_CreatesJob"

# Run with verbose output
dotnet test --verbosity normal

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Visual Studio

1. **Test Explorer**: View → Test Explorer
2. **Run All**: Click "Run All" button
3. **Run Specific**: Right-click test → Run
4. **Debug**: Right-click test → Debug

### CI/CD (GitHub Actions)

```yaml
name: Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '10.0.x'
    
    - name: Restore dependencies
      run: dotnet restore
    
    - name: Build
      run: dotnet build --no-restore
    
    - name: Run unit tests
      run: dotnet test tests/Milvaion.UnitTests --no-build --verbosity normal
    
    - name: Run integration tests
      run: dotnet test tests/Milvaion.IntegrationTests --no-build --verbosity normal
    
    - name: Generate coverage report
      run: dotnet test --collect:"XPlat Code Coverage"
    
    - name: Upload coverage to Codecov
      uses: codecov/codecov-action@v3
```

---

## Test Coverage

### Generating Coverage Reports

```bash
# Generate coverage
dotnet test --collect:"XPlat Code Coverage"

# Install ReportGenerator
dotnet tool install -g dotnet-reportgenerator-globaltool

# Generate HTML report
reportgenerator \
  -reports:"**/coverage.cobertura.xml" \
  -targetdir:"coveragereport" \
  -reporttypes:Html

# Open report
start coveragereport/index.html
```

### Coverage Goals

| Layer | Target | Rationale |
|-------|--------|-----------|
| Domain | 90%+ | Core business logic |
| Application | 85%+ | Use cases and orchestration |
| Infrastructure | 70%+ | External dependencies, harder to test |
| API Controllers | 80%+ | Thin layer, focus on routing |

---

## Best Practices

### DO

✅ **Use descriptive test names**
```csharp
[Fact]
public void Handle_InvalidCronExpression_ReturnsValidationError()
```

✅ **Follow AAA pattern (Arrange, Act, Assert)**
```csharp
// Arrange
var job = new ScheduledJob { ... };

// Act
var result = await handler.Handle(command);

// Assert
result.IsSuccess.Should().BeTrue();
```

✅ **Test one thing per test**
```csharp
// Good - focused test
[Fact]
public void Disable_SetsIsActiveToFalse()

// Bad - testing multiple things
[Fact]
public void Disable_SetsIsActiveToFalse_AndUpdatesModificationDate_AndLogsAction()
```

✅ **Use test data builders**
```csharp
var job = new JobBuilder()
    .WithDisplayName("Test")
    .Inactive()
    .Build();
```

✅ **Mock external dependencies**
```csharp
_emailServiceMock.Setup(x => x.SendAsync(...)).ReturnsAsync(true);
```

### DON'T

❌ **Don't test framework code**
```csharp
// Bad - testing EF Core
[Fact]
public void DbContext_CanSaveEntity() { ... }
```

❌ **Don't use Thread.Sleep in tests**
```csharp
// Bad
Thread.Sleep(5000);

// Good
await Task.Delay(100, cts.Token);
```

❌ **Don't depend on test execution order**
```csharp
// Bad - assumes Test1 runs first
[Fact]
public void Test2_DependsOnTest1() { ... }
```

❌ **Don't share state between tests**
```csharp
// Bad
private static int _counter;

[Fact]
public void Test1() { _counter++; }

[Fact]
public void Test2() { Assert.Equal(1, _counter); } // Flaky!
```

### Test Naming Examples

```csharp
// Good test names
Handle_ValidCommand_CreatesJob
Handle_InvalidCronExpression_ReturnsError
Handle_DuplicateJobName_ThrowsException
ExecuteAsync_CancellationRequested_StopsExecution
GetJobs_WithFilter_ReturnsFilteredResults

// Bad test names
Test1
TestCreateJob
ItWorks
CreateJobTest
```

---

## Further Reading

- [Development Guide](./DEVELOPMENT.md)
- [Contributing Guide](./CONTRIBUTING.md)
- [xUnit Documentation](https://xunit.net/)
- [Moq Documentation](https://github.com/moq/moq4)
- [FluentAssertions Documentation](https://fluentassertions.com/)
