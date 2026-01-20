# Development Guide

This guide provides detailed instructions for setting up and working with the Milvaion development environment.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Environment Setup](#environment-setup)
- [Project Structure](#project-structure)
- [Running the Application](#running-the-application)
- [Database Management](#database-management)
- [Testing](#testing)
- [Debugging](#debugging)
- [UI Development](#ui-development)
- [Worker Development](#worker-development)
- [Troubleshooting](#troubleshooting)

---

## Prerequisites

### Required Software

| Software | Version | Download |
|----------|---------|----------|
| .NET SDK | 10.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Docker Desktop | 20.10+ | [docker.com](https://www.docker.com/products/docker-desktop) |
| Git | 2.30+ | [git-scm.com](https://git-scm.com/downloads) |
| Node.js | 18+ | [nodejs.org](https://nodejs.org/) |
| Visual Studio | 2022/2026 | [visualstudio.com](https://visualstudio.microsoft.com/) |

### Recommended VS Extensions

- C# Dev Kit
- Docker
- GitLens
- REST Client
- EditorConfig

### Recommended VS Code Extensions (Alternative)

- C# (ms-dotnettools.csharp)
- C# Dev Kit
- Docker
- GitLens
- REST Client
- EditorConfig

---

## Environment Setup

### 1. Clone the Repository

```bash
git clone https://github.com/Milvasoft/milvaion.git
cd milvaion
```

### 2. Start Infrastructure Services

Create and start PostgreSQL, Redis, RabbitMQ, and Seq:

```bash
docker compose -f docker-compose.infra.yml up -d
```

Verify services are running:

```bash
docker compose -f docker-compose.infra.yml ps
```

Expected output:
```
NAME                STATUS          PORTS
milvaion-postgres   Up (healthy)    5432->5432
milvaion-redis      Up (healthy)    6379->6379
milvaion-rabbitmq   Up (healthy)    5672->5672, 15672->15672
milvaion-seq        Up              5341->5341, 80->80
```

### 3. Infrastructure Access

| Service | URL | Credentials |
|---------|-----|-------------|
| RabbitMQ Management | http://localhost:15672 | guest / guest |
| Seq | http://localhost:5341 | (no auth in dev) |
| PostgreSQL | localhost:5432 | postgres / postgres123 |
| Redis | localhost:6379 | (no auth in dev) |

### 4. Restore Dependencies

```bash
dotnet restore
```

### 5. Apply Database Migrations

```bash
cd src/Milvaion.Api
dotnet ef database update
```

Or run the API - it auto-migrates on startup in development:

```bash
dotnet run
```

### 6. Verify Setup

Access the API documentation:
- **Scalar UI**: http://localhost:5000/api/documentation/index.html
- **Dashboard**: http://localhost:5000

---

## Project Structure

```
milvaion/
├── src/
│   ├── Milvaion.Domain/              # Domain entities and enums
│   │   ├── Entities/
│   │   ├── Enums/
│   │   └── JsonModels/
│   │
│   ├── Milvaion.Application/         # Application layer (CQRS)
│   │   ├── Behaviours/               # MediatR pipeline behaviors
│   │   ├── Dtos/                     # Data transfer objects
│   │   ├── Features/                 # Commands and queries by feature
│   │   │   ├── Jobs/
│   │   │   ├── Occurrences/
│   │   │   ├── Workers/
│   │   │   └── Users/
│   │   ├── Interfaces/               # Repository and service interfaces
│   │   └── Utils/
│   │
│   ├── Milvaion.Infrastructure/      # Infrastructure implementations
│   │   ├── Extensions/               # DI extensions
│   │   ├── Persistence/              # EF Core, repositories
│   │   ├── Services/                 # External service implementations
│   │   │   ├── Messaging/            # RabbitMQ
│   │   │   ├── Caching/              # Redis
│   │   │   └── Scheduling/           # Job scheduling
│   │   └── Utils/
│   │
│   ├── Milvaion.Api/                 # API layer
│   │   ├── AppStartup/               # Startup configuration
│   │   ├── Controllers/              # API controllers
│   │   ├── Middlewares/              # Custom middlewares
│   │   ├── Migrations/               # EF Core migrations
│   │   ├── BackgroundServices/       # Background services
│   │   ├── Hubs/                     # SignalR hubs
│   │   ├── StaticFiles/              # SQL scripts, templates
│   │   └── wwwroot/                  # Static files
│   │
│   ├── Sdk/
│   │   ├── Milvasoft.Milvaion.Sdk/        # Client SDK
│   │   └── Milvasoft.Milvaion.Sdk.Worker/ # Worker SDK
│   │
│   ├── Workers/
│   │   ├── HttpWorker/               # HTTP request worker
│   │   ├── SqlWorker/                # SQL execution worker
│   │   ├── EmailWorker/              # Email sending worker
│   │   ├── MilvaionMaintenanceWorker/ # Maintenance worker
│   │   └── SampleWorker/             # Example worker
│   │
│   └── MilvaionUI/                   # React dashboard
│       ├── src/
│       ├── public/
│       └── package.json
│
├── tests/
│   ├── Milvaion.UnitTests/           # Unit tests
│   └── Milvaion.IntegrationTests/    # Integration tests
│
├── docs/
│   ├── portaldocs/                   # User documentation
│   ├── githubdocs/                   # Developer documentation
│   └── src/                          # Images and assets
│
├── build/                            # Build scripts
│   ├── build-api.ps1
│   ├── build-worker.ps1
│   └── build-all.ps1
│
├── docker-compose.yml                # Full stack compose
├── docker-compose.infra.yml          # Infrastructure only
└── Milvaion.sln                      # Solution file
```

---

## Running the Application

### Running the API

**Using CLI:**
```bash
cd src/Milvaion.Api
dotnet run
```

**Using Visual Studio:**
1. Open `Milvaion.sln`
2. Set `Milvaion.Api` as startup project
3. Press F5 or click "Start"

**Environment Variables:**
```bash
# Development mode
ASPNETCORE_ENVIRONMENT=Development
MILVA_ENV=dev

# Override connection strings
ConnectionStrings__DefaultConnectionString=Host=localhost;Port=5432;...
MilvaionConfig__Redis__ConnectionString=localhost:6379
```

### Running Workers

**Sample Worker:**
```bash
cd src/Workers/SampleWorker
dotnet run
```

**HTTP Worker:**
```bash
cd src/Workers/HttpWorker
dotnet run
```

**Run multiple workers:**
```bash
# Terminal 1
cd src/Workers/SampleWorker && dotnet run

# Terminal 2
cd src/Workers/EmailWorker && dotnet run

# Terminal 3
cd src/Workers/HttpWorker && dotnet run
```

### Running with Docker

**Full stack:**
```bash
docker compose up -d
```

**API only (with local workers):**
```bash
docker compose -f docker-compose.infra.yml up -d
cd src/Milvaion.Api && dotnet run
```

---

## Database Management

### Creating Migrations

```bash
cd src/Milvaion.Api

# Create a new migration
dotnet ef migrations add MigrationName

# Create migration with specific context
dotnet ef migrations add MigrationName --context MilvaionDbContext
```

### Applying Migrations

```bash
# Apply all pending migrations
dotnet ef database update

# Apply to specific migration
dotnet ef database update MigrationName

# Rollback to previous migration
dotnet ef database update PreviousMigrationName
```

### Viewing Migration SQL

```bash
# Generate SQL script
dotnet ef migrations script -o migration.sql

# Generate idempotent script
dotnet ef migrations script --idempotent -o migration.sql
```

### Database Reset (Development Only)

```bash
# Drop and recreate database
dotnet ef database drop --force
dotnet ef database update
```

### Connecting to PostgreSQL

```bash
# Using psql
docker exec -it milvaion-postgres psql -U postgres -d MilvaionDb

# Common queries
\dt                          # List tables
\d "ScheduledJobs"           # Describe table
SELECT * FROM "ScheduledJobs" LIMIT 10;
```

---

## Testing

### Running Tests

```bash
# All tests
dotnet test

# Unit tests only
dotnet test tests/Milvaion.UnitTests

# Integration tests (requires infrastructure)
dotnet test tests/Milvaion.IntegrationTests

# With verbose output
dotnet test --verbosity normal

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Writing Unit Tests

```csharp
public class CreateJobCommandHandlerTests
{
    private readonly Mock<IJobRepository> _repositoryMock;
    private readonly CreateJobCommandHandler _handler;

    public CreateJobCommandHandlerTests()
    {
        _repositoryMock = new Mock<IJobRepository>();
        _handler = new CreateJobCommandHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesJob()
    {
        // Arrange
        var command = new CreateJobCommand(new CreateJobDto
        {
            DisplayName = "Test Job",
            WorkerId = "test-worker"
        });

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _repositoryMock.Verify(x => x.AddAsync(It.IsAny<ScheduledJob>()), Times.Once);
    }
}
```

### Writing Integration Tests

```csharp
public class JobsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public JobsControllerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateJob_ValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new { displayName = "Test Job", workerId = "worker-1" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/v1/jobs/job", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
```

### Test Configuration

Tests use `appsettings.Testing.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnectionString": "Host=localhost;Port=5432;Database=MilvaionTestDb;..."
  },
  "MilvaionConfig": {
    "Redis": {
      "ConnectionString": "localhost:6379",
      "Database": 1
    }
  }
}
```

---

## Debugging

### Visual Studio Debugging

1. Set breakpoints in code
2. Press F5 to start debugging
3. Use Debug windows: Locals, Watch, Call Stack

### Remote Debugging (Docker)

```dockerfile
# In Dockerfile, use Debug configuration
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS debug
WORKDIR /app
COPY . .
RUN dotnet build -c Debug
EXPOSE 5000
EXPOSE 4024  # VSDBG port
ENTRYPOINT ["dotnet", "run", "--no-build"]
```

### Logging for Debugging

```csharp
// Increase log level in appsettings.Development.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.EntityFrameworkCore": "Information",
      "Milvaion": "Debug"
    }
  }
}
```

### Viewing Logs

**Console:**
Logs appear in terminal when running with `dotnet run`

**Seq:**
Open http://localhost:5341 to view structured logs

**Filter by correlation:**
```
CorrelationId = "abc-123"
```

---

## UI Development

### Setup

```bash
cd src/MilvaionUI

# Install dependencies
npm install

# Start development server
npm run dev
```

### Development Workflow

1. API runs on http://localhost:5000
2. UI dev server runs on http://localhost:5173
3. Vite proxies API requests to backend

### Building for Production

```bash
cd src/MilvaionUI
npm run build
```

Output is copied to `src/Milvaion.Api/wwwroot/` during build.

### UI Structure

```
MilvaionUI/
├── src/
│   ├── components/         # Reusable components
│   ├── pages/              # Page components
│   ├── hooks/              # Custom React hooks
│   ├── services/           # API service layer
│   ├── stores/             # State management
│   ├── types/              # TypeScript types
│   └── utils/              # Utility functions
├── public/
└── package.json
```

---

## Worker Development

### Creating a New Worker

```bash
# Install template
dotnet new install Milvasoft.Templates.Milvaion

# Create worker project
dotnet new milvaion-console-worker -n MyCompany.MyWorker
```

### Worker Structure

```
MyWorker/
├── Program.cs              # Entry point
├── appsettings.json        # Configuration
├── Jobs/
│   └── MyJob.cs            # Job implementations
└── Dockerfile
```

### Implementing Jobs

```csharp
public class MyJob : IAsyncJob
{
    private readonly ILogger<MyJob> _logger;
    private readonly IMyService _myService;

    public MyJob(ILogger<MyJob> logger, IMyService myService)
    {
        _logger = logger;
        _myService = myService;
    }

    public async Task ExecuteAsync(IJobContext context)
    {
        // Log to user-friendly logs (visible in dashboard)
        context.LogInformation("Starting job...");

        // Parse job data
        var data = JsonSerializer.Deserialize<MyJobData>(context.Job.JobData);

        // Check cancellation
        context.CancellationToken.ThrowIfCancellationRequested();

        // Execute business logic
        await _myService.ProcessAsync(data, context.CancellationToken);

        context.LogInformation("Job completed!");
    }
}
```

### Registering Services

```csharp
// Program.cs
var builder = Host.CreateApplicationBuilder(args);

// Register your services
builder.Services.AddScoped<IMyService, MyService>();
builder.Services.AddHttpClient<IExternalApi, ExternalApiClient>();

// Register Milvaion Worker SDK
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

### Job Configuration

```json
{
  "JobConsumers": {
    "MyJob": {
      "ConsumerId": "my-job-consumer",
      "MaxParallelJobs": 10,
      "ExecutionTimeoutSeconds": 300,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 10
    }
  }
}
```

---

## Troubleshooting

### Common Issues

#### "Connection refused" to PostgreSQL

```bash
# Check if container is running
docker ps | grep postgres

# Check logs
docker logs milvaion-postgres

# Restart container
docker compose -f docker-compose.infra.yml restart postgres
```

#### "Connection refused" to RabbitMQ

```bash
# Check container status
docker logs milvaion-rabbitmq

# Verify management UI works
curl http://localhost:15672
```

#### EF Core migration errors

```bash
# Reset migrations
rm -rf src/Milvaion.Api/Migrations/*
dotnet ef migrations add Initial
dotnet ef database update
```

#### Port already in use

```bash
# Find process using port
netstat -ano | findstr :5000

# Kill process (Windows)
taskkill /PID <PID> /F

# Or change port in launchSettings.json
```

#### Worker not receiving jobs

1. Check worker ID matches job's `workerId`
2. Verify RabbitMQ connection
3. Check queue bindings in RabbitMQ Management UI
4. Review worker logs for errors

### Getting Help

- Check existing [GitHub Issues](https://github.com/Milvasoft/milvaion/issues)
- Ask in [Discussions](https://github.com/Milvasoft/milvaion/discussions)
- Review [Documentation](../portaldocs/00-guide.md)

---

## Next Steps

- [Architecture Guide](./ARCHITECTURE.md) - Understand the system design
- [Contributing Guide](./CONTRIBUTING.md) - Contribute to the project
- [API Reference](./API-REFERENCE.md) - API documentation
- [Worker SDK Guide](./WORKER-SDK.md) - Build custom workers
