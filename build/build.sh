#!/bin/bash
# Quick Docker build script for Milvaion API
# Usage: ./build.sh [TAG] [--skip-push]

set -e

REGISTRY="milvasoft"
TAG="${1:-latest}"
SKIP_PUSH_FLAG=""

if [ "$2" = "--skip-push" ]; then
    SKIP_PUSH_FLAG="--skip-push"
fi

echo "========================================"
echo "   Milvaion API - Docker Hub Build"
echo "========================================"
echo "   Registry: $REGISTRY"
echo "   Tag:      $TAG"
echo "   Push:     $([ -z "$SKIP_PUSH_FLAG" ] && echo "Enabled" || echo "Disabled")"
echo "========================================"
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

bash "$SCRIPT_DIR/build-api.sh" -r "$REGISTRY" -t "$TAG" $SKIP_PUSH_FLAG

if [ $? -eq 0 ]; then
    echo ""
    echo "[SUCCESS] Build complete!"
    echo ""
    echo "Image: $REGISTRY/milvaion-api:$TAG"
    echo ""
    if [ -z "$SKIP_PUSH_FLAG" ]; then
        echo "Pull with: docker pull $REGISTRY/milvaion-api:$TAG"
    fi
    echo ""
else
    echo ""
    echo "[ERROR] Build failed!"
    exit 1
fi