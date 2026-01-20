#!/bin/bash

#
# Milvaion SQL Worker Docker Build Script
# Builds and pushes the SQL Worker Docker image to a registry.
#
# Usage:
#   ./build-sql-worker.sh -r milvasoft -t 1.0.0
#   ./build-sql-worker.sh -r ghcr.io/milvasoft -t latest --skip-push
#

set -e

# Default values
TAG="latest"
SKIP_PUSH=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
MAGENTA='\033[0;35m'
NC='\033[0m' # No Color

# Helper functions
print_info() { echo -e "${CYAN}[INFO]  $1${NC}"; }
print_success() { echo -e "${GREEN}[OK] $1${NC}"; }
print_error() { echo -e "${RED}[ERROR] $1${NC}"; }
print_step() { echo -e "\n${YELLOW}[STEP] $1${NC}"; }

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -r|--registry)
            REGISTRY="$2"
            shift 2
            ;;
        -t|--tag)
            TAG="$2"
            shift 2
            ;;
        --skip-push)
            SKIP_PUSH=true
            shift
            ;;
        -h|--help)
            echo "Usage: $0 -r <registry> [-t <tag>] [--skip-push]"
            echo ""
            echo "Options:"
            echo "  -r, --registry   Docker registry (required)"
            echo "  -t, --tag        Image tag (default: latest)"
            echo "  --skip-push      Build only, don't push"
            echo ""
            echo "Examples:"
            echo "  $0 -r milvasoft -t 1.0.0"
            echo "  $0 -r ghcr.io/milvasoft -t latest --skip-push"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Validate required parameters
if [ -z "$REGISTRY" ]; then
    print_error "Registry is required. Use -r option."
    echo "Usage: $0 -r <registry> [-t <tag>]"
    exit 1
fi

# Get script directory and root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
DOCKERFILE="$ROOT_DIR/src/Workers/SqlWorker/Dockerfile"

echo ""
echo -e "${MAGENTA}===============================================================${NC}"
echo -e "${MAGENTA}           Milvaion SQL Worker Build Script${NC}"
echo -e "${MAGENTA}===============================================================${NC}"
echo -e "${MAGENTA}  Registry: $REGISTRY${NC}"
echo -e "${MAGENTA}  Tag:      $TAG${NC}"
echo -e "${MAGENTA}  Push:     $(if $SKIP_PUSH; then echo "Disabled"; else echo "Enabled"; fi)${NC}"
echo -e "${MAGENTA}===============================================================${NC}"
echo ""

# Validate Dockerfile exists
if [ ! -f "$DOCKERFILE" ]; then
    print_error "Dockerfile not found: $DOCKERFILE"
    exit 1
fi

# Build Docker Image
print_step "Building Docker image..."
IMAGE_NAME="$REGISTRY/milvaion-sql-worker:$TAG"

print_info "Building $IMAGE_NAME..."
cd "$ROOT_DIR"
docker build -t "$IMAGE_NAME" -f "$DOCKERFILE" .

print_success "Image built successfully"

# Tag as latest
if [ "$TAG" != "latest" ]; then
    print_step "Tagging image as 'latest'..."
    docker tag "$IMAGE_NAME" "$REGISTRY/milvaion-sql-worker:latest"
    print_success "Image tagged as 'latest'"
fi

# Push to Registry
if [ "$SKIP_PUSH" = false ]; then
    print_step "Pushing image to registry..."

    print_info "Pushing $IMAGE_NAME..."
    docker push "$IMAGE_NAME"

    if [ "$TAG" != "latest" ]; then
        print_info "Pushing $REGISTRY/milvaion-sql-worker:latest..."
        docker push "$REGISTRY/milvaion-sql-worker:latest"
    fi

    print_success "Image pushed to registry"
else
    print_info "Push skipped"
fi

# Summary
echo ""
echo -e "${GREEN}===============================================================${NC}"
echo -e "${GREEN}                       Build Complete!${NC}"
echo -e "${GREEN}===============================================================${NC}"
echo -e "${GREEN}  Docker Image: $IMAGE_NAME${NC}"
echo -e "${GREEN}===============================================================${NC}"
echo ""
echo -e "${CYAN}[INFO] Next steps:${NC}"
echo "  Deploy with Docker:"
echo "     docker run -d --network milvaion_network $IMAGE_NAME"
echo ""
echo "  Or with Docker Compose:"
echo "     docker-compose up -d sql-worker"
