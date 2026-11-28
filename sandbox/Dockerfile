# syntax=docker/dockerfile:1
# Toolchain sandbox for HambotV2. Build context is this sandbox/ directory; the
# repo itself is bind-mounted at /work/repo by compose.yaml.
#
# Base image: the .NET 10 SDK (same tag the bot's own Dockerfile builds with).
FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG DEBIAN_FRONTEND=noninteractive
# Match the host account so files written into the bind-mounted repo keep host
# ownership: SANDBOX_UID=$(id -u) SANDBOX_GID=$(id -g) docker compose build
ARG USER_UID=1000
ARG USER_GID=1000

RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      git \
      git-lfs \
      jq \
      less \
      procps \
      unzip \
      vim-tiny \
    && rm -rf /var/lib/apt/lists/*

# git's ownership check for the bind-mounted working tree.
RUN git lfs install --system \
 && git config --system --add safe.directory '*'

RUN groupadd --gid "${USER_GID}" dev \
 && useradd --uid "${USER_UID}" --gid "${USER_GID}" --create-home \
      --shell /bin/bash dev \
 && mkdir -p /home/dev/.nuget/packages \
 && chown -R dev:dev /home/dev

ENV HOME=/home/dev \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

USER dev
WORKDIR /work/repo
CMD ["bash"]
