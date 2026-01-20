# SqlWorker Dockerfile - Optimized Multi-stage Build
# Base runtime image (use regular image instead of chiseled)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

# Install required libraries for PostgreSQL and RabbitMQ
RUN apt-get update && apt-get install -y \
    libgssapi-krb5-2 \
    procps \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files for restore (layer caching optimization)
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/Milvasoft.Milvaion.Sdk.csproj", "Sdk/Milvasoft.Milvaion.Sdk/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker/Milvasoft.Milvaion.Sdk.Worker.csproj", "Sdk/Milvasoft.Milvaion.Sdk.Worker/"]
COPY ["src/Workers/SqlWorker/SqlWorker.csproj", "Workers/SqlWorker/"]

# Restore dependencies
RUN dotnet restore "Workers/SqlWorker/SqlWorker.csproj"

# Copy source code
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/", "Sdk/Milvasoft.Milvaion.Sdk/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk.Worker/", "Sdk/Milvasoft.Milvaion.Sdk.Worker/"]
COPY ["src/Workers/SqlWorker/", "Workers/SqlWorker/"]

# Build and publish in one step (simpler and avoids path issues)
WORKDIR "/src/Workers/SqlWorker"
RUN dotnet publish "SqlWorker.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Final runtime stage
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Health check (optional but recommended)
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD pgrep -f "dotnet.*SqlWorker" > /dev/null || exit 1

# Entry point
ENTRYPOINT ["dotnet", "SqlWorker.dll"]
