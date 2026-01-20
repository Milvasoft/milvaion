#!/bin/bash
set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info() { echo -e "${CYAN}[INFO]  $1${NC}"; }
success() { echo -e "${GREEN}[OK] $1${NC}"; }
error() { echo -e "${RED}[ERROR] $1${NC}"; exit 1; }
step() { echo -e "\n${YELLOW}[STEP] $1${NC}"; }

# Default values
REGISTRY=""
TAG="latest"
SKIP_PUSH=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -r|--registry) REGISTRY="$2"; shift 2 ;;
        -t|--tag) TAG="$2"; shift 2 ;;
        --skip-push) SKIP_PUSH=true; shift ;;
        -h|--help)
            echo "Usage: $0 -r REGISTRY [-t TAG] [--skip-push]"
            echo ""
            echo "Options:"
            echo "  -r, --registry    Docker registry (e.g., milvasoft)"
            echo "  -t, --tag         Image tag (default: latest)"
            echo "  --skip-push       Build image but don't push"
            echo ""
            echo "Example:"
            echo "  $0 -r milvasoft -t 1.0.0"
            exit 0
            ;;
        *) error "Unknown option: $1" ;;
    esac
done

if [ -z "$REGISTRY" ]; then
    error "Registry is required. Use -r or --registry"
fi

# Paths
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
DOCKERFILE="$ROOT_DIR/src/Workers/SampleWorker/Dockerfile"

echo -e "${CYAN}===============================================================${NC}"
echo -e "${CYAN}           Milvaion Sample Worker Build Script            ${NC}"
echo -e "${CYAN}===============================================================${NC}"
echo -e "${CYAN}  Registry: $REGISTRY"
echo -e "${CYAN}  Tag:      $TAG"
echo -e "${CYAN}  Push:     $([ "$SKIP_PUSH" = true ] && echo "Disabled" || echo "Enabled")"
echo -e "${CYAN}===============================================================${NC}"

# Build Docker Image
step "Building Docker image..."
IMAGE_NAME="$REGISTRY/milvaion-sampleworker:$TAG"

info "Building $IMAGE_NAME..."
cd "$ROOT_DIR"
docker build -t "$IMAGE_NAME" -f "$DOCKERFILE" . || error "Docker build failed"
success "Image built successfully"

# Tag as latest
if [ "$TAG" != "latest" ]; then
    step "Tagging image as 'latest'..."
    docker tag "$IMAGE_NAME" "$REGISTRY/milvaion-sampleworker:latest"
    success "Image tagged as 'latest'"
fi

# Push to Registry
if [ "$SKIP_PUSH" = false ]; then
    step "Pushing image to registry..."
    
    info "Pushing $IMAGE_NAME..."
    docker push "$IMAGE_NAME" || error "Docker push failed"
    
    if [ "$TAG" != "latest" ]; then
        info "Pushing $REGISTRY/milvaion-sampleworker:latest..."
        docker push "$REGISTRY/milvaion-sampleworker:latest" || error "Docker push (latest) failed"
    fi
    
    success "Image pushed to registry"
else
    info "Push skipped"
fi

# Summary
echo -e "\n${GREEN}===============================================================${NC}"
echo -e "${GREEN}                       Build Complete!                     ${NC}"
echo -e "${GREEN}===============================================================${NC}"
echo -e "${GREEN}  Docker Image: $IMAGE_NAME${NC}"
echo -e "${GREEN}===============================================================${NC}"

echo -e "\n${CYAN}[INFO] Next steps:${NC}"
echo -e "  Deploy with Docker:"
echo -e "     docker run -d --network milvaion_network $IMAGE_NAME"
echo -e "\n  Or scale with Docker Compose:"
echo -e "     docker-compose up --scale worker=4 -d"
