# Contributing to Milvaion

First off, thank you for considering contributing to Milvaion! It's people like you that make Milvaion such a great tool.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Workflow](#development-workflow)
- [Style Guidelines](#style-guidelines)
- [Commit Messages](#commit-messages)
- [Pull Request Process](#pull-request-process)
- [Community](#community)

---

## Code of Conduct

This project and everyone participating in it is governed by our [Code of Conduct](../../CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code. Please report unacceptable behavior to the project maintainers.

---

## Getting Started

### Prerequisites

Before you begin, ensure you have the following installed:

- **.NET 10 SDK** - [Download](https://dotnet.microsoft.com/download)
- **Docker Desktop** - [Download](https://www.docker.com/products/docker-desktop)
- **Git** - [Download](https://git-scm.com/downloads)
- **Node.js 18+** - [Download](https://nodejs.org/) (for UI development)
- **Visual Studio 2022/2026** or **VS Code** with C# extension

### Setting Up the Development Environment

1. **Fork the repository** on GitHub

2. **Clone your fork**
   ```bash
   git clone https://github.com/YOUR_USERNAME/milvaion.git
   cd milvaion
   ```

3. **Add upstream remote**
   ```bash
   git remote add upstream https://github.com/Milvasoft/milvaion.git
   ```

4. **Start infrastructure services**
   ```bash
   docker compose -f docker-compose.infra.yml up -d
   ```

5. **Restore dependencies**
   ```bash
   dotnet restore
   ```

6. **Run the API**
   ```bash
   cd src/Milvaion.Api
   dotnet run
   ```

7. **Run tests to verify setup**
   ```bash
   dotnet test
   ```

---

## How Can I Contribute?

### Reporting Bugs

Before creating bug reports, please check existing issues to avoid duplicates.

**When creating a bug report, include:**

- **Clear title** describing the issue
- **Steps to reproduce** the behavior
- **Expected behavior** vs **actual behavior**
- **Environment details** (OS, .NET version, Docker version)
- **Logs and error messages** (sanitized of sensitive data)
- **Screenshots** if applicable

Use the bug report template when creating issues.

### Suggesting Features

Feature suggestions are welcome! Before creating a feature request:

- Check if the feature already exists
- Check if there's an existing feature request
- Consider if the feature aligns with Milvaion's goals

**When suggesting a feature, include:**

- **Clear description** of the feature
- **Use case** - why is this feature needed?
- **Proposed solution** (if you have one)
- **Alternatives considered**

### Contributing Code

#### Good First Issues

Look for issues labeled `good first issue` - these are great for newcomers.

#### Areas Where We Need Help

- **Documentation** - Improving docs, adding examples
- **Testing** - Adding unit tests, integration tests
- **Bug fixes** - Fixing reported issues
- **Features** - Implementing approved feature requests
- **Performance** - Optimizing critical paths
- **Security** - Security improvements and audits

---

## Development Workflow

### Branch Naming Convention

```
feature/: New features. Example: feature/login-system
bugfix/: Bug fixes. Example: bugfix/header-styling
hotfix/: Critical production fixes. Example: hotfix/critical-security-issue
release/: Release preparation. Example: release/v1.0.1
docs/: Documentation changes. Example: docs/api-endpoints
experimental/: Experimental features. Example: experimental/new-algorithm
wip/: Work in progress. Example: wip/refactor-auth-system
```

### Creating a Feature Branch

```bash
# Sync with upstream
git fetch upstream
git checkout main
git merge upstream/main

# Create feature branch
git checkout -b feature/my-awesome-feature
```

### Making Changes

1. Make your changes in small, logical commits
2. Write or update tests as needed
3. Ensure all tests pass locally
4. Update documentation if needed

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Milvaion.UnitTests

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run integration tests (requires infrastructure)
dotnet test tests/Milvaion.IntegrationTests
```

### Building the Project

```bash
# Build all projects
dotnet build

# Build in Release mode
dotnet build -c Release

# Build Docker images
cd build
./build-all.ps1 -Registry "local" -Tag "dev" -SkipPush
```

---

## Style Guidelines

### C# Coding Standards

We follow the [Microsoft C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions) with these additions:

#### Naming

- **PascalCase** for public members, types, namespaces
- **camelCase** for private fields (with `_` prefix)
- **UPPER_CASE** for constants
- **Async suffix** for async methods

```csharp
public class JobService
{
    private readonly IJobRepository _jobRepository;
    private const int MAX_RETRY_COUNT = 5;
    
    public async Task<Job> GetJobAsync(Guid id) { }
}
```

#### Code Organization

```csharp
public class MyClass
{
    // 1. Constants
    // 2. Static fields
    // 3. Instance fields
    // 4. Constructors
    // 5. Properties
    // 6. Public methods
    // 7. Private methods
}
```

#### Best Practices

- Use `var` when the type is obvious
- Prefer `async/await` over `.Result` or `.Wait()`
- Use nullable reference types (`string?`)
- Prefer records for DTOs
- Use expression-bodied members when appropriate
- Always use braces for control statements

### Documentation

- XML comments on all public APIs
- README in each major component folder
- Update relevant docs when changing behavior

```csharp
/// <summary>
/// Executes the specified job with the given context.
/// </summary>
/// <param name="context">The job execution context.</param>
/// <returns>A task representing the asynchronous operation.</returns>
/// <exception cref="JobExecutionException">Thrown when job execution fails.</exception>
public async Task ExecuteAsync(IJobContext context)
```

---

## Commit Messages

We follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

```
<type>(<scope>): <subject>

[optional body]

[optional footer(s)]
```

### Types

| Type | Description |
|------|-------------|
| `feat` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation only |
| `style` | Formatting, missing semicolons, etc. |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `perf` | Performance improvement |
| `test` | Adding or updating tests |
| `chore` | Maintenance tasks |
| `ci` | CI/CD changes |

### Scopes

| Scope | Description |
|-------|-------------|
| `api` | Milvaion.Api changes |
| `worker` | Worker SDK changes |
| `domain` | Domain layer changes |
| `infra` | Infrastructure changes |
| `ui` | Dashboard UI changes |
| `docs` | Documentation |
| `tests` | Test changes |
| `build` | Build system changes |

### Examples

```
feat(api): add job tagging support

fix(worker): resolve memory leak in long-running jobs

docs(readme): update quick start guide

refactor(domain): extract job validation logic

test(api): add integration tests for job endpoints
```

---

## Pull Request Process

### Before Submitting

1. Code compiles without warnings
2. All tests pass
3. Code follows style guidelines
4. Documentation updated (if needed)
5. Commit messages follow conventions
6. Branch is up to date with main

### Creating the Pull Request

1. Push your branch to your fork
   ```bash
   git push origin feature/my-awesome-feature
   ```

2. Open a Pull Request against `main` branch

3. Fill out the PR template completely

4. Link related issues using keywords (`Fixes #123`, `Closes #456`)

### PR Template

```markdown
## Description
Brief description of changes

## Type of Change
- Bug fix
- New feature
- Breaking change
- Documentation update

## How Has This Been Tested?
Describe testing approach

## Checklist
- Code follows style guidelines
- Self-review completed
- Documentation updated
- Tests added/updated
- All tests passing
```

### Review Process

1. At least one maintainer must approve
2. All CI checks must pass
3. No unresolved conversations
4. Branch must be up to date

### After Merge

- Delete your feature branch
- Sync your fork with upstream

```bash
git checkout main
git fetch upstream
git merge upstream/main
git push origin main
```

---

## Project Structure

Understanding the project structure helps in contributing effectively:

```
milvaion/
??? src/
?   ??? Milvaion.Domain/        # Core domain entities, enums
?   ??? Milvaion.Application/   # Use cases, DTOs, interfaces
?   ??? Milvaion.Infrastructure/# EF Core, external services
?   ??? Milvaion.Api/           # REST API, controllers
?   ??? Sdk/                    # Client and Worker SDKs
?   ??? Workers/                # Built-in workers
?   ??? MilvaionUI/             # React dashboard
??? tests/
?   ??? Milvaion.UnitTests/
?   ??? Milvaion.IntegrationTests/
??? docs/
?   ??? portaldocs/             # User documentation
?   ??? githubdocs/             # Developer documentation
??? build/                      # Build scripts
```

---

## Getting Help

- ?? Read the [Documentation](../portaldocs/00-guide.md)
- ?? Ask in [Discussions](https://github.com/Milvasoft/milvaion/discussions)
- ?? Check existing [Issues](https://github.com/Milvasoft/milvaion/issues)
- ?? Email us at milvasoft@milvasoft.com

---

## Recognition

Contributors are recognized in:

- GitHub contributors page
- Release notes for significant contributions
- Special thanks in documentation for major features
- Mention in CHANGELOG.md for impactful contributions

---

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

---

Thank you for contributing to Milvaion! ??


