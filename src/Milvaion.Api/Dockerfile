#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# ============================================
# Stage 1: Build React Frontend
# ============================================
FROM node:20-alpine AS frontend-build

WORKDIR /app/frontend

# Copy package files
COPY ["src/MilvaionUI/package.json", "src/MilvaionUI/package-lock.json*", "./"]

# Install dependencies
RUN npm ci --silent

# Copy source files
COPY ["src/MilvaionUI/", "./"]

# Build production bundle
RUN npm run build

# ============================================
# Stage 2: .NET Runtime Base
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base

# Install required libraries for PostgreSQL and RabbitMQ
RUN apt-get update && apt-get install -y \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

# ============================================
# Stage 3: Build .NET Backend
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY *.sln .

COPY ["tests/Milvaion.UnitTests/Milvaion.UnitTests.csproj", "Milvaion.UnitTests/"]
COPY ["tests/Milvaion.IntegrationTests/Milvaion.IntegrationTests.csproj", "Milvaion.IntegrationTests/"]
COPY ["src/Milvaion.Application/Milvaion.Application.csproj", "Milvaion.Application/"]
COPY ["src/Milvaion.Domain/Milvaion.Domain.csproj", "Milvaion.Domain/"]
COPY ["src/Milvaion.Infrastructure/Milvaion.Infrastructure.csproj", "Milvaion.Infrastructure/"]
COPY ["src/Milvaion.Api/Milvaion.Api.csproj", "Milvaion.Api/"]
COPY ["src/Sdk/Milvasoft.Milvaion.Sdk/Milvasoft.Milvaion.Sdk.csproj", "Sdk/Milvasoft.Milvaion.Sdk/"]

RUN dotnet restore "./Milvaion.Api/Milvaion.Api.csproj"

COPY ./src/. .

RUN dotnet build "./Milvaion.Api/Milvaion.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# ============================================
# Stage 4: Publish .NET Backend
# ============================================
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# ✅ Set flag to skip React build in csproj (already built in Stage 1)
ENV DOCKER_BUILD=true
RUN dotnet publish "./Milvaion.Api/Milvaion.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ============================================
# Stage 5: Final Production Image
# ============================================
FROM base AS final
WORKDIR /app

# Copy backend publish output
COPY --from=publish /app/publish .

# ✅ Copy React build to wwwroot (served by ASP.NET Core)
COPY --from=frontend-build /app/frontend/dist ./wwwroot

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD curl -f http://localhost:5000/api/health || exit 1

ENTRYPOINT ["dotnet", "Milvaion.Api.dll"]