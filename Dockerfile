# syntax=docker/dockerfile:1
# Self-host image for the pol-core hosts (Api / Worker) + a migrate target that
# bootstraps DB principals and applies EF migrations. One Dockerfile, parameterized by HOST_PROJECT/HOST_DLL.
ARG DOTNET_VERSION=10.0

# ---- restore: source + restored packages, shared by build + migrate ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src
COPY . .
RUN dotnet restore pol-core.slnx

# ---- build: publish the selected host ----
FROM restore AS build
ARG HOST_PROJECT
RUN dotnet publish "${HOST_PROJECT}" -c Release -o /app --no-restore

# ---- final: small runtime image, non-root, /health on 8080 ----
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh \
    && adduser --disabled-password --gecos '' --uid 10001 appuser
USER appuser
ARG HOST_DLL
ENV HOST_DLL=${HOST_DLL} \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]

# ---- migrate: one-shot — bootstrap principals (sqlcmd) then apply EF migrations. Runs from source.
# Derives from `restore` (NOT `build`) so it never runs the host publish — it only needs source + EF tooling.
FROM restore AS migrate
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg apt-transport-https ca-certificates \
    && curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -sSL https://packages.microsoft.com/config/debian/12/prod.list \
        | sed 's#deb https#deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] https#' > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 \
    && rm -rf /var/lib/apt/lists/*
ENV PATH="/opt/mssql-tools18/bin:/root/.dotnet/tools:${PATH}"
RUN dotnet tool install --global dotnet-ef --version 10.0.8
COPY docker/migrate-entrypoint.sh /usr/local/bin/migrate-entrypoint.sh
RUN chmod +x /usr/local/bin/migrate-entrypoint.sh
ENTRYPOINT ["/usr/local/bin/migrate-entrypoint.sh"]
