# syntax=docker/dockerfile:1
# ---------------------------------------------------------------- egress proxy
FROM debian:trixie-slim AS egress

RUN apt-get update \
 && apt-get install -y --no-install-recommends tinyproxy \
 && rm -rf /var/lib/apt/lists/*

COPY tinyproxy.conf /etc/tinyproxy/tinyproxy.conf
COPY allowlist.txt /etc/tinyproxy/allowlist.txt
# A Windows checkout with core.autocrlf hands the build context CRLF files and
# tinyproxy refuses CR at the end of a directive — normalize the copies.
RUN sed -i 's/\r$//' /etc/tinyproxy/tinyproxy.conf /etc/tinyproxy/allowlist.txt \
 && chmod 0644 /etc/tinyproxy/tinyproxy.conf /etc/tinyproxy/allowlist.txt

USER tinyproxy
EXPOSE 8888
ENTRYPOINT ["tinyproxy", "-d", "-c", "/etc/tinyproxy/tinyproxy.conf"]

# ------------------------------------------------------------------ agent box
# Same toolchain as the flavor-A sandbox (sandbox/Dockerfile) plus the agent CLI.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent

ARG DEBIAN_FRONTEND=noninteractive
ARG AGENT=vibe                          # 'none' builds the same box without a CLI
ARG AGENT_CLI_VERSION=3.0.2

RUN apt-get update && apt-get install -y --no-install-recommends \
      build-essential \
      ca-certificates \
      curl \
      git \
      git-lfs \
      gosu \
      iproute2 \
      jq \
      less \
      libffi-dev \
      pkg-config \
      procps \
      unzip \
    && rm -rf /var/lib/apt/lists/*

# uv (standalone) + the agent CLI in a build-scoped dir, so later
# `uv tool install` by the agent lands in its home volume, not the read-only
# rootfs. Managed pythons go to /opt/uv-python (image layer; readable at
# runtime). Python is pinned to 3.14: some of the CLI's dependencies (PyO3
# wheels) do not support newer interpreters yet.
ENV UV_PYTHON_INSTALL_DIR=/opt/uv-python \
    UV_PYTHON=3.14
RUN curl -LsSf https://astral.sh/uv/install.sh \
      | UV_INSTALL_DIR=/usr/local/bin UV_NO_MODIFY_PATH=1 sh -s -- \
 && if [ "$AGENT" != none ]; then \
      UV_TOOL_DIR=/opt/uv-tools UV_TOOL_BIN_DIR=/usr/local/bin \
        /usr/local/bin/uv tool install "mistral-vibe==${AGENT_CLI_VERSION}"; \
    fi

RUN git lfs install --system \
 && git config --system --add safe.directory '*'

# The .NET SDK base image already ships a uid/gid-1000 account (ubuntu) — free
# the id, then create the agent account the compose file and entrypoint expect.
RUN if getent passwd 1000 >/dev/null; then userdel "$(getent passwd 1000 | cut -d: -f1)"; fi \
 && if getent group 1000 >/dev/null; then groupdel "$(getent group 1000 | cut -d: -f1)"; fi \
 && groupadd --gid 1000 agent \
 && useradd --uid 1000 --gid 1000 --create-home --shell /bin/bash agent \
 && mkdir -p /work /home/agent/.cache \
 && chown -R agent:agent /work /home/agent \
 && if [ -d /opt/uv-tools ]; then chown -R agent:agent /opt/uv-tools /opt/uv-python; fi

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY xdg-open.sh /usr/local/bin/xdg-open
# Same CRLF normalization as the egress config: the scripts run from their
# shebang, and a CRLF checkout breaks that inside a Linux container.
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh /usr/local/bin/xdg-open \
 && chmod 0755 /usr/local/bin/entrypoint.sh /usr/local/bin/xdg-open

ENV HOME=/home/agent \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

WORKDIR /work/repo
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["bash"]
