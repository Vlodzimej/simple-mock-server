# syntax=docker/dockerfile:1

FROM node:24-bookworm-slim AS frontend
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN mkdir -p public && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /source
COPY global.json ./
COPY src/MockServer.Core/MockServer.Core.csproj src/MockServer.Core/
COPY src/MockServer.Infrastructure/MockServer.Infrastructure.csproj src/MockServer.Infrastructure/
COPY src/MockServer.Api/MockServer.Api.csproj src/MockServer.Api/
RUN dotnet restore src/MockServer.Api/MockServer.Api.csproj
COPY src/ src/
COPY --from=frontend /web/dist/web/browser/ src/MockServer.Api/wwwroot/
RUN dotnet publish src/MockServer.Api/MockServer.Api.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data \
    && chown "$APP_UID:$APP_UID" /data
COPY --from=backend /out/ ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    Urls=http://0.0.0.0:8080 \
    DatabasePath=/data/mock.db
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "MockServer.Api.dll"]
