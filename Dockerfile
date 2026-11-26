FROM node:24-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/MockServer.Api/MockServer.Api.csproj src/MockServer.Api/
COPY src/MockServer.Core/MockServer.Core.csproj src/MockServer.Core/
COPY src/MockServer.Infrastructure/MockServer.Infrastructure.csproj src/MockServer.Infrastructure/
RUN dotnet restore src/MockServer.Api/MockServer.Api.csproj
COPY src/ src/
COPY --from=web /src/web/dist/web/browser/ src/MockServer.Api/wwwroot/
RUN dotnet publish src/MockServer.Api/MockServer.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER root
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
# The base image defaults to port 8080. Clear it so only --urls is used.
ENV ASPNETCORE_HTTP_PORTS=
EXPOSE 5080
ENTRYPOINT ["dotnet", "MockServer.Api.dll", "--urls", "http://0.0.0.0:5080"]
