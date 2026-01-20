
<h1 align="center">Milvaion</h1>

<p align="center">
  <img src="https://portal.milvasoft.com/assets/images/logo256-e8d874bf50d543bf1319f5cbd1effba5.png" alt="MilvaionLogo"  />
</p>

A distributed job scheduling system built on .NET 10

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=flat-square&logo=postgresql)](https://www.postgresql.org/)
[![Redis](https://img.shields.io/badge/Redis-7-DC382D?style=flat-square&logo=redis)](https://redis.io/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-3.x-FF6600?style=flat-square&logo=rabbitmq)](https://www.rabbitmq.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)

[![Documentation](https://img.shields.io/badge/docs%20-official%20web-blue)](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/milvaion-doc-guide)
[![Worker Templates NuGet](https://img.shields.io/nuget/v/Milvasoft.Helpers)](https://www.nuget.org/packages/Milvasoft.Templates.Milvaion/) [![Worker Sdk NuGet](https://img.shields.io/nuget/dt/Milvasoft.Helpers)](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker/)

---

## What is Milvaion?

Milvaion is a **distributed job scheduling system** that separates the *scheduler* (API that decides when jobs run) from the *workers* (processes that execute jobs), connected via Redis and RabbitMQ.

```
┌─────────────────┐        ┌─────────────────┐       ┌─────────────────┐
│  Milvaion API   │        │    RabbitMQ     │       │    Workers      │
│  (Scheduler)    │───────>│  (Job Queue)    │──────>│  (Executors)    │
│                 │        │                 │       │                 │
│ • REST API      │        │ • Job messages  │       │ • IJob classes  │
│ • Dashboard     │        │ • Status queues │       │ • Retry logic   │
│ • Cron parsing  │<───────│ • Log streams   │<──────│ • DI support    │
└─────────────────┘        └─────────────────┘       └─────────────────┘
```

### Why Milvaion?

Most job schedulers run jobs **inside the same process** as the scheduling logic. This works fine until:

- A long-running job blocks other jobs from executing
- A crashing job takes down the entire scheduler
- You need different hardware for different job types (e.g., GPU for ML jobs)
- You want to scale job execution independently from the API

Milvaion solves these problems by **completely separating scheduling from execution**.

---

## Features

### Reliability
- **At-least-once delivery** via RabbitMQ manual ACK
- **Automatic retries** with exponential backoff
- **Dead Letter Queue** for failed jobs after max retries
- **Zombie detection** recovers stuck jobs
- **Auto disable** always failing jobs (configurable threshold)

### Scalability
- **Horizontal worker scaling** - add more workers for more throughput
- **Job-type routing** - route specific jobs to specialized workers
- **Independent scaling** - scale API and workers separately

### Observability
- **Real-time dashboard** with SignalR updates
- **Execution logs** - User-friendly logs stored in occurrences + technical logs to Seq
- **Worker health monitoring** via heartbeats
- **OpenTelemetry support** for metrics and tracing

### Developer Experience
- **Simple `IJob` interfaces** - implement one method
- **Full DI support** - inject services into jobs
- **Auto-discovery** - jobs registered automatically
- **Cancellation support** - graceful shutdown
- **Project templates** - get started quickly with `dotnet new`

### Built-in Workers
- **HTTP Worker** - Call REST APIs on schedule
- **SQL Worker** - Execute database queries
- **Email Worker** - Send emails via SMTP
- **Maintenance Worker** - Milvaion self data warehouse cleanup and archival

---

## Quick Start

### Prerequisites

- **Docker Desktop** (v20.10+) with Docker Compose
- **Web browser** for the dashboard

### 1. Start the Stack

```bash
# Clone the repository
git clone https://github.com/Milvasoft/milvaion.git
cd milvaion

# Start all services
docker compose up -d
```

### 2. Access the Dashboard

Open **http://localhost:5000** in your browser.

- Default username: `rootuser`
- Get password: `docker logs milvaion-api 2>&1 | grep -i "password"`

### 3. Create Your First Job

```bash
curl -X POST http://localhost:5000/api/v1/jobs/job \
  -H "Content-Type: application/json" \
  -d '{
    "displayName": "My First Job",
    "workerId": "sample-worker-01",
    "selectedJobName": "SampleJob",
    "cronExpression": "* * * * *",
    "isActive": true,
    "jobData": "{\"message\": \"Hello from Milvaion!\"}"
  }'
```

📖 **[Full Quick Start Guide →](./docs/portaldocs/02-quick-start.md)**

---

## Architecture

Milvaion follows **Onion Architecture** principles with clear separation of concerns:

![Architecture](./docs/portaldocs/src/architecture.png)

### Solution Structure

```
milvaion/
├── src/
│   ├── Core/
│   │   ├── Milvaion.Domain/          # Entities, enums, domain logic
│   │   └── Milvaion.Application/     # Use cases, DTOs, interfaces
│   ├── Infrastructure/
│   │   └── Milvaion.Infrastructure/  # EF Core, external services
│   ├── Presentation/
│   │   └── Milvaion.Api/             # REST API, controllers, dashboard
│   ├── Sdk/
│   │   ├── Milvasoft.Milvaion.Sdk/        # Client SDK
│   │   └── Milvasoft.Milvaion.Sdk.Worker/ # Worker SDK
│   ├── Workers/
│   │   ├── HttpWorker/               # Built-in HTTP worker
│   │   ├── SqlWorker/                # Built-in SQL worker
│   │   ├── EmailWorker/              # Built-in Email worker
│   │   └── MilvaionMaintenanceWorker/ # Maintenance jobs
│   └── MilvaionUI/                   # React dashboard
├── tests/
│   ├── Milvaion.UnitTests/
│   └── Milvaion.IntegrationTests/
├── docs/
│   ├── portaldocs/                   # User documentation
│   └── githubdocs/                   # Developer documentation
└── build/                            # Build scripts
```

### Project Dependencies

![Project Dependencies](./docs/src/project-dependencies.png)

**Build Order:** Domain → Application → Infrastructure → Api → Tests

---

## Development Setup

### Prerequisites

- **.NET 10 SDK**
- **PostgreSQL 16**
- **Redis 7**
- **RabbitMQ 3.x**
- **Node.js 18+** (for UI development)

### Local Development

```bash
# Clone repository
git clone https://github.com/Milvasoft/milvaion.git
cd milvaion

# Start infrastructure (PostgreSQL, Redis, RabbitMQ)
docker compose -f docker-compose.infra.yml up -d

# Run the API
cd src/Milvaion.Api
dotnet run

# Run a worker (in another terminal)
cd src/Workers/SampleWorker
dotnet run
```

### Running Tests

```bash
# Unit tests
dotnet test tests/Milvaion.UnitTests

# Integration tests (requires infrastructure)
dotnet test tests/Milvaion.IntegrationTests

# All tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Building Docker Images

```bash
cd build

# Build all images
./build-all.ps1 -Registry "milvasoft" -Tag "1.0.0"

# Build API only
./build-api.ps1 -Registry "milvasoft" -Tag "1.0.0"

# Build Worker only
./build-worker.ps1 -Registry "milvasoft" -Tag "1.0.0"
```

---

## Creating a Worker

### 1. Install the Template

```bash
dotnet new install Milvasoft.Templates.Milvaion
```

### 2. Create a New Worker

```bash
dotnet new milvaion-console-worker -n MyCompany.MyWorker
cd MyCompany.MyWorker
```

### 3. Implement a Job

```csharp
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;

public class MyCustomJob : IAsyncJob
{
    private readonly IMyService _myService;
    
    public MyCustomJob(IMyService myService)
    {
        _myService = myService;
    }
    
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting my custom job...");
        
        var data = JsonSerializer.Deserialize<MyJobData>(context.Job.JobData);
        
        await _myService.ProcessAsync(data, context.CancellationToken);
        
        context.LogInformation("Job completed successfully!");
    }
}
```

📖 **[Full Worker Guide →](./docs/portaldocs/04-your-first-worker.md)**

---

## Documentation

### User Documentation (Portal Docs)

| Document | Description |
|----------|-------------|
| [Introduction](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/introduction) | What is Milvaion, when to use it |
| [Quick Start](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/quick-start) | Get running in under 10 minutes |
| [Core Concepts](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/core-concepts) | Architecture and key terms |
| [Your First Worker](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/your-first-worker) | Create a custom worker |
| [Implementing Jobs](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/implementing-jobs) | Advanced job patterns |
| [Configuration](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/configuration) | All configuration options |
| [Deployment](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/deployment) | Docker and Kubernetes deployment |
| [Reliability](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/reliability) | Retry, DLQ, zombie detection |
| [Scaling](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/scaling) | Horizontal scaling strategies |
| [Monitoring](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/monitoring) | Health checks, metrics, logging |

### Developer Documentation (GitHub Docs)

| Document | Description |
|----------|-------------|
| [Contributing](./docs/githubdocs/CONTRIBUTING.md) | How to contribute |
| [Architecture](./docs/githubdocs/ARCHITECTURE.md) | Technical architecture deep-dive |
| [Development](./docs/githubdocs/DEVELOPMENT.md) | Development environment setup |
| [API Reference](./docs/githubdocs/API-REFERENCE.md) | REST API documentation |
| [Worker SDK](./docs/githubdocs/WORKER-SDK.md) | Worker SDK reference |
| [Security](./docs/githubdocs/SECURITY.md) | Security policies |

---

## Tech Stack

| Component | Technology |
|-----------|------------|
| **Backend** | .NET 10, ASP.NET Core |
| **Database** | PostgreSQL 16, Entity Framework Core |
| **Cache/Scheduling** | Redis 7 |
| **Message Queue** | RabbitMQ 3.x |
| **Frontend** | React, TypeScript, Vite |
| **Real-time** | SignalR |
| **Logging** | Serilog, Seq |
| **Metrics** | OpenTelemetry, Prometheus, Grafana |
| **Testing** | xUnit, FluentAssertions, Testcontainers |
| **CI/CD** | GitHub Actions, Docker |

### Key Libraries

- **CQRS**: MediatR, Milvasoft.Components.CQRS
- **Data Access**: Npgsql.EntityFrameworkCore.PostgreSQL, Milvasoft.DataAccess.EfCore
- **Authentication**: JWT Bearer, Milvasoft.Identity
- **API**: Asp.Versioning.Mvc, Scalar (OpenAPI)
- **Validation**: FluentValidation
- **Messaging**: RabbitMQ.Client

### Design Patterns Used

- CQRS (Command Query Responsibility Segregation)
- Mediator Pattern
- Repository Pattern
- Factory Pattern
- Outbox Pattern (for offline resilience)
- Leader Election (for dispatcher)

---

## Contributing

We welcome contributions! Please see our [Contributing Guide](./docs/githubdocs/CONTRIBUTING.md) for details.

### Quick Contribution Steps

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Run tests (`dotnet test`)
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to the branch (`git push origin feature/amazing-feature`)
7. Open a Pull Request

Please read our [Code of Conduct](./CODE_OF_CONDUCT.md) before contributing.

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## Support

- 📖 [Documentation](./docs/portaldocs/00-guide.md)
- 🐛 [Issue Tracker](https://github.com/Milvasoft/milvaion/issues)
- 💬 [Discussions](https://github.com/Milvasoft/milvaion/discussions)

---

<p align="center">
  Made with ❤️ by <a href="https://github.com/Milvasoft">Milvasoft</a>
</p>
