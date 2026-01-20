#!/bin/bash
set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

# Default values
REGISTRY=""
TAG="latest"
SKIP_PUSH=false
SKIP_API=false
SKIP_WORKER=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -r|--registry) REGISTRY="$2"; shift 2 ;;
        -t|--tag) TAG="$2"; shift 2 ;;
        --skip-push) SKIP_PUSH=true; shift ;;
        --skip-api) SKIP_API=true; shift ;;
        --skip-worker) SKIP_WORKER=true; shift ;;
        -h|--help)
            echo "Usage: $0 -r REGISTRY [-t TAG] [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  -r, --registry     Docker registry (e.g., milvasoft)"
            echo "  -t, --tag          Image tag (default: latest)"
            echo "  --skip-push        Build images but don't push"
            echo "  --skip-api         Skip building API image"
            echo "  --skip-worker      Skip building Worker image"
            echo ""
            echo "Example:"
            echo "  $0 -r milvasoft -t 1.0.0"
            exit 0
            ;;
        *) echo -e "${RED}Unknown option: $1${NC}"; exit 1 ;;
    esac
done

if [ -z "$REGISTRY" ]; then
    echo -e "${RED}Registry is required. Use -r or --registry${NC}"
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo -e "${CYAN}===============================================================${NC}"
echo -e "${CYAN}              Milvaion Build All - Docker Hub              ${NC}"
echo -e "${CYAN}===============================================================${NC}"
echo -e "${CYAN}  Registry: $REGISTRY"
echo -e "${CYAN}  Tag:      $TAG"
echo -e "${CYAN}  API:      $([ "$SKIP_API" = true ] && echo "Skipped" || echo "Enabled")"
echo -e "${CYAN}  Worker:   $([ "$SKIP_WORKER" = true ] && echo "Skipped" || echo "Enabled")"
echo -e "${CYAN}  Push:     $([ "$SKIP_PUSH" = true ] && echo "Disabled" || echo "Enabled")"
echo -e "${CYAN}===============================================================${NC}"

TOTAL_STEPS=0
CURRENT_STEP=0

[ "$SKIP_API" = false ] && ((TOTAL_STEPS++))
[ "$SKIP_WORKER" = false ] && ((TOTAL_STEPS++))

# Build API
if [ "$SKIP_API" = false ]; then
    ((CURRENT_STEP++))
    echo -e "\n${YELLOW}[$CURRENT_STEP/$TOTAL_STEPS] Building Milvaion API...${NC}"
    
    if [ "$SKIP_PUSH" = true ]; then
        bash "$SCRIPT_DIR/build-api.sh" -r "$REGISTRY" -t "$TAG" --skip-push
    else
        bash "$SCRIPT_DIR/build-api.sh" -r "$REGISTRY" -t "$TAG"
    fi
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}[ERROR] API build failed!${NC}"
        exit 1
    fi
fi

# Build Worker
if [ "$SKIP_WORKER" = false ]; then
    ((CURRENT_STEP++))
    echo -e "\n${YELLOW}[$CURRENT_STEP/$TOTAL_STEPS] Building Milvaion Worker...${NC}"
    
    if [ "$SKIP_PUSH" = true ]; then
        bash "$SCRIPT_DIR/build-worker.sh" -r "$REGISTRY" -t "$TAG" --skip-push
    else
        bash "$SCRIPT_DIR/build-worker.sh" -r "$REGISTRY" -t "$TAG"
    fi
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}[ERROR] Worker build failed!${NC}"
        exit 1
    fi
fi

# Summary
echo -e "\n${GREEN}===============================================================${NC}"
echo -e "${GREEN}                   All Builds Complete!                   ${NC}"
echo -e "${GREEN}===============================================================${NC}"
[ "$SKIP_API" = false ] && echo -e "${GREEN}  [OK] API:    $REGISTRY/milvaion-api:$TAG"
[ "$SKIP_WORKER" = false ] && echo -e "${GREEN}  [OK] Worker: $REGISTRY/milvaion-sampleworker:$TAG"
echo -e "${GREEN}===============================================================${NC}"

if [ "$SKIP_PUSH" = false ]; then
    echo -e "\n${CYAN}[INFO] Images available on Docker Hub:${NC}"
    [ "$SKIP_API" = false ] && echo -e "  docker pull $REGISTRY/milvaion-api:$TAG"
    [ "$SKIP_WORKER" = false ] && echo -e "  docker pull $REGISTRY/milvaion-sampleworker:$TAG"
fi

echo -e "\n${CYAN}[INFO] Deploy with Docker Compose:${NC}"
echo -e "  docker-compose up -d"
