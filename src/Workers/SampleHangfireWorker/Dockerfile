# SampleHangfireWorker Dockerfile - Optimized Multi-stage Build
# Base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute AS base

# Install required libraries for PostgreSQL and RabbitMQ
RUN apt-get update && apt-get install -y \
    libgssapi-krb5-2 \
    procps \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r milvaion && useradd -r -g milvaion milvaion

WORKDIR /app

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:11.0-resolute AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files for restore (layer caching optimization)
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/Milvasoft.Milvaion.Sdk.csproj", "Sdk/Milvasoft.Milvaion.Sdk/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker/Milvasoft.Milvaion.Sdk.Worker.csproj", "Sdk/Milvasoft.Milvaion.Sdk.Worker/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker.Hangfire/Milvasoft.Milvaion.Sdk.Worker.Hangfire.csproj", "Sdk/Milvasoft.Milvaion.Sdk.Worker.Hangfire/"]
COPY ["src/Workers/SampleHangfireWorker/SampleHangfireWorker.csproj", "Workers/SampleHangfireWorker/"]

# Restore dependencies
RUN dotnet restore "Workers/SampleHangfireWorker/SampleHangfireWorker.csproj"

# Copy source code
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/", "Sdk/Milvasoft.Milvaion.Sdk/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker/", "Sdk/Milvasoft.Milvaion.Sdk.Worker/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker.Hangfire/", "Sdk/Milvasoft.Milvaion.Sdk.Worker.Hangfire/"]
COPY ["src/Workers/SampleHangfireWorker/", "Workers/SampleHangfireWorker/"]

# Build and publish in one step
WORKDIR "/src/Workers/SampleHangfireWorker"
RUN dotnet publish "SampleHangfireWorker.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

# Final runtime stage
FROM base AS final
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Create worker_data directory for offline resilience
RUN mkdir -p /app/worker_data && chown -R milvaion:milvaion /app

# Switch to non-root user
USER milvaion

# Environment variables (can be overridden)
ENV DOTNET_ENVIRONMENT=Production
ENV Worker__WorkerId=hangfire-worker
ENV Worker__RabbitMQ__Host=rabbitmq
ENV Worker__RabbitMQ__Port=5672
ENV Worker__Redis__ConnectionString=redis:6379

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD test -f /tmp/live || exit 1

ENTRYPOINT ["dotnet", "SampleHangfireWorker.dll"]