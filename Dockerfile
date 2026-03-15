# ── Build stage ───────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DotAgent.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# System tools + Python + Node.js
RUN apt-get update && apt-get install -y \
    bash \
    curl \
    wget \
    git \
    jq \
    tree \
    findutils \
    procps \
    ca-certificates \
    gnupg \
    # Python
    python3 \
    python3-dev \
    python3-venv \
    # Node.js via NodeSource (latest LTS)
    && curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

# Install uv (Astral's Python package manager)
RUN curl -LsSf https://astral.sh/uv/install.sh | sh
ENV PATH="/root/.local/bin:$PATH"

# Install Context7 CLI (documentation lookups for Plan mode)
RUN npm install -g @upstash/context7-mcp && \
    npx -y @upstash/context7-mcp --version || echo "Context7 npx fallback available"

# Create workspace dir
RUN mkdir -p /workspace

# Copy published .NET app
COPY --from=build /app/publish .

# Verify tools
RUN echo "=== Toolchain ===" \
    && python3 --version \
    && node --version \
    && npm --version \
    && uv --version \
    && (ctx7 --version 2>/dev/null || echo "ctx7: using npx fallback")

ENV TERM=xterm-256color
ENV DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION=1

WORKDIR /workspace
ENTRYPOINT ["dotnet", "/app/DotAgent.dll"]