FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Project files first, so restore is cached until a dependency actually changes.
COPY src/Domain/*.csproj src/Domain/
COPY src/Application/*.csproj src/Application/
COPY src/Infrastructure/*.csproj src/Infrastructure/
COPY src/Api/*.csproj src/Api/
RUN dotnet restore src/Api/SaasMultiTenant.Api.csproj

COPY src/ src/
RUN dotnet publish src/Api/SaasMultiTenant.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# The runtime image ships without curl, and the compose health check needs it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# A non root user, because the application never needs to write to its own
# image and a container that cannot escalate is one fewer thing to reason about.
RUN useradd --create-home --shell /usr/sbin/nologin api
USER api

COPY --from=build --chown=api:api /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl --fail http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "SaasMultiTenant.Api.dll"]
