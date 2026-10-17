#!/usr/bin/env bash
#
# 在 macOS/Linux 上用 Docker 构建 amd64 deb。构建机不需要安装 .NET、Node 或 dpkg。
#
# 用法：
#   ./packaging/linux/build-deb.sh
#   ./packaging/linux/build-deb.sh --version 0.2.0
#   ./packaging/linux/build-deb.sh --output /tmp/poolpilot-deb
#
# 环境变量：
#   PLATFORM          Docker 目标平台，默认 linux/amd64（Apple Silicon 上由 Docker 模拟）。
#   SHOW_AUTH_LINKS   前端是否显示登录/注册入口，默认 true；false/0 隐藏（构建时内联）。

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"

VERSION="${VERSION:-0.1.0}"
OUTPUT_DIR="$REPO_ROOT/packaging/out"
PLATFORM="${PLATFORM:-linux/amd64}"
SHOW_AUTH_LINKS="${SHOW_AUTH_LINKS:-true}"
IMAGE="poolpilot-deb:build"

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)
            VERSION="${2:?--version 需要一个版本号}"
            shift 2
            ;;
        --output)
            OUTPUT_DIR="${2:?--output 需要一个目录}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "未知参数：$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ ! "$VERSION" =~ ^[0-9][A-Za-z0-9.+~-]*$ ]]; then
    echo "版本号不符合 Debian 版本格式：$VERSION" >&2
    exit 1
fi

command -v docker >/dev/null 2>&1 || {
    echo "未找到 docker。请先安装 Docker Desktop 或 Docker Engine。" >&2
    exit 1
}

mkdir -p "$OUTPUT_DIR"

echo "构建 $PLATFORM 的 PoolPilot $VERSION ..."
docker build \
    --platform "$PLATFORM" \
    --build-arg "VERSION=$VERSION" \
    --build-arg "VITE_SHOW_AUTH_LINKS=$SHOW_AUTH_LINKS" \
    -f "$SCRIPT_DIR/Dockerfile" \
    -t "$IMAGE" \
    "$REPO_ROOT"

CONTAINER_ID="$(docker create --platform "$PLATFORM" "$IMAGE")"
cleanup() {
    docker rm -f "$CONTAINER_ID" >/dev/null 2>&1 || true
}
trap cleanup EXIT

DEB_NAME="poolpilot_${VERSION}_amd64.deb"
docker cp "$CONTAINER_ID:/build/out/$DEB_NAME" "$OUTPUT_DIR/$DEB_NAME"

echo "已生成: $OUTPUT_DIR/$DEB_NAME"
du -h "$OUTPUT_DIR/$DEB_NAME"
