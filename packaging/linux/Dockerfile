# syntax=docker/dockerfile:1.7
#
# 在容器内完成前端构建、后端自包含发布与 deb 组装。
# 构建机只需要 Docker；Node/.NET/dpkg-deb 的版本由下面三个基础镜像固定。

ARG NODE_IMAGE=node:22-bookworm-slim
ARG DOTNET_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DEBIAN_IMAGE=debian:bookworm-slim

FROM ${NODE_IMAGE} AS web
ARG VITE_SHOW_AUTH_LINKS=true
ENV VITE_SHOW_AUTH_LINKS=${VITE_SHOW_AUTH_LINKS}
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM ${DOTNET_IMAGE} AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src ./src
RUN dotnet publish src/PoolPilot.Api/PoolPilot.Api.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained true \
      -m:1 \
      -p:DebugType=None \
      -p:DebugSymbols=false \
      -o /out/api

FROM ${DEBIAN_IMAGE} AS package
ARG VERSION=0.1.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends dpkg-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build

COPY --from=api /out/api ./stage/opt/poolpilot/api
COPY --from=web /src/web/dist ./stage/opt/poolpilot/api/wwwroot
COPY packaging/linux/debian ./debian
COPY packaging/linux/assemble-deb.sh ./assemble-deb.sh

RUN chmod +x ./assemble-deb.sh \
    && ./assemble-deb.sh "$VERSION"

CMD ["/bin/true"]
