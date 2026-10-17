# Linux 打包

产物是 `packaging/out/poolpilot_<version>_amd64.deb`。完整构建、安装与配置说明见
[docs/DEPLOY.md](../../docs/DEPLOY.md)。

```bash
./packaging/linux/build-deb.sh --version 0.1.0
```

构建机只需要 Docker；Dockerfile 内完成前端构建、自包含发布与 deb 组装。
