#!/usr/bin/env bash
#
# 在容器内组装 Debian 包。输入约定（由 Dockerfile 准备）：
#   /build/stage/opt/poolpilot/api  发布产物 + wwwroot
#   /build/debian                    control 模板与生命周期脚本
# 输出：
#   /build/out/poolpilot_<version>_amd64.deb
#
# 也可以在有 dpkg-deb 的 Linux 主机上手工调用；macOS 请走 build-deb.sh（Docker）。

set -euo pipefail

VERSION="${1:?用法: assemble-deb.sh <version>}"
ROOT="${ROOT:-/build}"
STAGE="$ROOT/stage"
DEBIAN="$STAGE/DEBIAN"
OUT="$ROOT/out"
TEMPLATES="$ROOT/debian"
PACKAGE="poolpilot"
ARCH="amd64"

if [[ ! "$VERSION" =~ ^[0-9][A-Za-z0-9.+~-]*$ ]]; then
    echo "版本号不符合 Debian 版本格式：$VERSION（应以数字开头，只含 A-Z a-z 0-9 . + ~ -）" >&2
    exit 1
fi

test -d "$STAGE/opt/poolpilot/api" || {
    echo "缺少发布产物：$STAGE/opt/poolpilot/api" >&2
    exit 1
}
test -f "$STAGE/opt/poolpilot/api/wwwroot/index.html" || {
    echo "缺少前端产物：$STAGE/opt/poolpilot/api/wwwroot/index.html" >&2
    exit 1
}

mkdir -p \
    "$DEBIAN" \
    "$OUT" \
    "$STAGE/lib/systemd/system" \
    "$STAGE/etc/default" \
    "$STAGE/usr/share/doc/$PACKAGE"

# 发布目录里不应该出现开发环境配置；误带进生产会让公开页开关等开发覆盖生效。
rm -f "$STAGE/opt/poolpilot/api/appsettings.Development.json"
find "$STAGE/opt/poolpilot/api" -type f -name '*.pdb' -delete

install -m 0644 "$TEMPLATES/poolpilot.service" "$STAGE/lib/systemd/system/poolpilot.service"
install -m 0644 "$TEMPLATES/poolpilot.default" "$STAGE/etc/default/poolpilot"
install -m 0644 "$TEMPLATES/README.Debian" "$STAGE/usr/share/doc/$PACKAGE/README.Debian"

# 代码与静态文件只读；唯一的可执行文件是 apphost。
find "$STAGE/opt/poolpilot/api" -type d -exec chmod 0755 {} +
find "$STAGE/opt/poolpilot/api" -type f -exec chmod 0644 {} +
chmod 0755 "$STAGE/opt/poolpilot/api/PoolPilot.Api"

INSTALLED_SIZE="$(du -sk "$STAGE" | awk '{print $1}')"

sed \
    -e "s/@VERSION@/$VERSION/g" \
    -e "s/@INSTALLED_SIZE@/$INSTALLED_SIZE/g" \
    "$TEMPLATES/control.in" > "$DEBIAN/control"

install -m 0644 "$TEMPLATES/conffiles" "$DEBIAN/conffiles"
install -m 0755 "$TEMPLATES/postinst" "$DEBIAN/postinst"
install -m 0755 "$TEMPLATES/prerm" "$DEBIAN/prerm"
install -m 0755 "$TEMPLATES/postrm" "$DEBIAN/postrm"

DEB="$OUT/${PACKAGE}_${VERSION}_${ARCH}.deb"

# --root-owner-group 让包内文件归 root，即使构建者在容器里不是 root（Dockerfile 里是 root）。
dpkg-deb --root-owner-group --build "$STAGE" "$DEB"

echo "已生成: $DEB"
du -h "$DEB"
