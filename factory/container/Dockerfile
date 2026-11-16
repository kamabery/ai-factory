# The deployed factory's image (docs/FACTORY-CONTRACT.md, "Cloud mode (deployed)"): the ai-factory package as a tool
# install, with git, node, claude, az, gh and terraform on PATH, run as a non-root user. Built by hand from the packed
# package, never from the repo root (which holds .env):
#
#   dotnet pack src/factory/Factory.Cli -c Release -o artifacts/container
#   docker build -f factory/container/Dockerfile --build-arg VERSION=<package version> \
#     -t ghcr.io/<owner>/ai-factory:<package version> artifacts/container
#
# The dashboard runs `factory container serve` (the default), the runner job `factory container job`.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble

ARG VERSION
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git gnupg lsb-release unzip \
 && install -d -m 0755 /etc/apt/keyrings \
 && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/githubcli.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
 && curl -fsSL https://apt.releases.hashicorp.com/gpg | gpg --dearmor -o /etc/apt/keyrings/hashicorp.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/hashicorp.gpg] https://apt.releases.hashicorp.com $(lsb_release -cs) main" > /etc/apt/sources.list.d/hashicorp.list \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
 && echo "deb [signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ $(lsb_release -cs) main" > /etc/apt/sources.list.d/azure-cli.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends nodejs gh terraform azure-cli \
 && npm install -g @anthropic-ai/claude-code \
 && npm cache clean --force \
 && rm -rf /var/lib/apt/lists/*

RUN useradd --create-home --shell /bin/bash factory
USER factory
WORKDIR /home/factory

COPY --chown=factory:factory ai-factory.${VERSION}.nupkg /tmp/pkg/
RUN dotnet tool install --global ai-factory --add-source /tmp/pkg --version ${VERSION} \
 && rm -rf /tmp/pkg \
 && git config --global user.name "AI factory" \
 && git config --global user.email "ai-factory@users.noreply.github.com"

ENV PATH="/home/factory/.dotnet/tools:${PATH}" \
    FACTORY_PROFILE=/home/factory/.ai-factory \
    FACTORY_WORK=/home/factory/work

EXPOSE 8080
ENTRYPOINT ["factory", "container"]
CMD ["serve"]
