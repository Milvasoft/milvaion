# GitHub Actions CI/CD

This repository uses GitHub Actions for automated builds and deployments.

## Workflows

### Docker Image Builds

The following workflows build and push Docker images to Docker Hub:

1. **Milvaion API** (`api-docker-build.yml`)
   - Triggers on changes to: `src/Milvaion.Api/`, `src/Milvaion.Application/`, `src/Milvaion.Domain/`, `src/Milvaion.Infrastructure/`, `src/MilvaionUI/`
   - Version file: `src/Milvaion.Api/VERSION`
   - Image tags: `milvaion-api:latest`, `milvaion-api:{VERSION}`

2. **HttpWorker** (`httpworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/HttpWorker/`
   - Version file: `src/Workers/HttpWorker/VERSION`
   - Image tags: `milvaion-http-worker:latest`, `milvaion-http-worker:{VERSION}`

3. **SqlWorker** (`sqlworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/SqlWorker/`
   - Version file: `src/Workers/SqlWorker/VERSION`
   - Image tags: `milvaion-sql-worker:latest`, `milvaion-sql-worker:{VERSION}`

4. **EmailWorker** (`emailworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/EmailWorker/`
   - Version file: `src/Workers/EmailWorker/VERSION`
   - Image tags: `milvaion-email-worker:latest`, `milvaion-email-worker:{VERSION}`

5. **MaintenanceWorker** (`maintenanceworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/MilvaionMaintenanceWorker/`
   - Version file: `src/Workers/MilvaionMaintenanceWorker/VERSION`
   - Image tags: `milvaion-maintenance-worker:latest`, `milvaion-maintenance-worker:{VERSION}`

6. **ReporterWorker** (`reporterworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/ReporterWorker/`
   - Version file: `src/Workers/ReporterWorker/VERSION`
   - Image tags: `milvaion-reporter-worker:latest`, `milvaion-reporter-worker:{VERSION}`

7. **SampleWorker** (`sampleworker-docker-build.yml`)
   - Triggers on changes to: `src/Workers/SampleWorker/`
   - Version file: `src/Workers/SampleWorker/VERSION`
   - Image tags: `milvaion-sample-worker:latest`, `milvaion-sample-worker:{VERSION}`

All of these wait for the `CI - Build & Test` run of the same commit (`wait-for-ci.yml`) and do not publish if it fails.
A manual `workflow_dispatch` run skips the wait. After a flaky CI failure, re-run CI and then the failed publish run.

### NuGet Package Publishes

The following workflows publish packages to NuGet.org:

1. **SDK Packages** (`sdk-nuget-publish.yml`)
   - Triggers on changes to: `src/Sdk/`
   - Publishes:
     - `Milvasoft.Milvaion.Sdk`
     - `Milvasoft.Milvaion.Sdk.Worker`
   - Version is determined from project files (.csproj)

2. **Worker Template** (`worker-template-nuget-publish.yml`)
   - Triggers on changes to: `src/Workers/Milvasoft.Templates.Milvaion/`
   - Publishes: `Milvasoft.Templates.Milvaion`
   - Version is determined from project file (.csproj)

Both wait for CI in the same way. On a pull request they only build and pack; nothing is pushed to NuGet.org.

### Helm Chart

**Helm Chart** (`helm.yml`)
- Triggers on changes to: `deploy/helm/`
- Runs `helm lint` and `helm template` for every values file in `deploy/helm/milvaion/ci/` and validates the output
  with kubeconform
- On a `helm-v<version>` tag (which must match `version` in `Chart.yaml`), pushes the chart to
  `oci://ghcr.io/<owner>/charts/milvaion` and attaches the package to a GitHub release. Uses the built-in
  `GITHUB_TOKEN`; no extra secrets.

## Version Management

### Docker Images

Docker image versions are managed via `VERSION` files in each project directory.

**To release a new version:**

1. Update the VERSION file in the respective project:
   ```bash
   echo "1.1.0" > src/Milvaion.Api/VERSION
   ```

2. Commit and push the change:
   ```bash
   git add src/Milvaion.Api/VERSION
   git commit -m "Bump API version to 1.1.0"
   git push origin master
   ```

3. The GitHub Action will automatically build and push:
   - `milvaion-api:latest`
   - `milvaion-api:1.1.0`

### NuGet Packages

NuGet package versions are managed in the project files (.csproj).

**To release a new version:**

1. Update the `<Version>` tag in the .csproj file:
   ```xml
   <PropertyGroup>
     <Version>1.2.0</Version>
   </PropertyGroup>
   ```

2. Commit and push the change:
   ```bash
   git add src/Sdk/Milvasoft.Milvaion.Sdk/Milvasoft.Milvaion.Sdk.csproj
   git commit -m "Bump SDK version to 1.2.0"
   git push origin master
   ```

3. The GitHub Action will automatically pack and publish to NuGet.org

## Required GitHub Secrets

Configure the following secrets in your GitHub repository settings:

- `DOCKERHUB_USERNAME` - Your Docker Hub username
- `DOCKERHUB_TOKEN` - Your Docker Hub access token
- `NUGET_API_KEY` - Your NuGet.org API key

## Required GitHub Variables

Configure the following variables in your GitHub repository settings:

- `DOTNET_VERSION` - .NET SDK version (e.g., `11.0.x`)

## Manual Trigger

All workflows can be manually triggered from the GitHub Actions tab using the "workflow_dispatch" event.

## Cache Strategy

Docker builds use GitHub Actions cache to speed up subsequent builds:
- `cache-from: type=gha` - Restore from cache
- `cache-to: type=gha,mode=max` - Save to cache

## Workflow Status

Check the status of workflows:
1. Go to the "Actions" tab in the GitHub repository
2. View recent workflow runs
3. Click on a specific run to see detailed logs
