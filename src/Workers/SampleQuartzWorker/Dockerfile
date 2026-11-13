# SampleQuartzWorker Dockerfile - Optimized Multi-stage Build
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
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker.Quartz/Milvasoft.Milvaion.Sdk.Worker.Quartz.csproj", "Sdk/Milvasoft.Milvaion.Sdk.Worker.Quartz/"]
COPY ["src/Workers/SampleQuartzWorker/SampleQuartzWorker.csproj", "Workers/SampleQuartzWorker/"]

# Restore dependencies
RUN dotnet restore "Workers/SampleQuartzWorker/SampleQuartzWorker.csproj"

# Copy source code
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/", "Sdk/Milvasoft.Milvaion.Sdk/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker/", "Sdk/Milvasoft.Milvaion.Sdk.Worker/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker.Quartz/", "Sdk/Milvasoft.Milvaion.Sdk.Worker.Quartz/"]
COPY ["src/Workers/SampleQuartzWorker/", "Workers/SampleQuartzWorker/"]

# Build and publish in one step
WORKDIR "/src/Workers/SampleQuartzWorker"
RUN dotnet publish "SampleQuartzWorker.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Final runtime stage
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Change ownership to non-root user
RUN chown -R milvaion:milvaion /app

# Switch to non-root user
USER milvaion

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD pgrep -f "dotnet.*SampleQuartzWorker" > /dev/null || exit 1

# Entry point
ENTRYPOINT ["dotnet", "SampleQuartzWorker.dll"]