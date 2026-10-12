# syntax=docker/dockerfile:1

# ---- Build ---------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless project files change).
COPY Directory.Build.props ./
COPY src/PodcastToYoto.Core/PodcastToYoto.Core.csproj src/PodcastToYoto.Core/
COPY src/PodcastToYoto.Web/PodcastToYoto.Web.csproj src/PodcastToYoto.Web/
RUN dotnet restore src/PodcastToYoto.Web/PodcastToYoto.Web.csproj

COPY src/ src/
# .git isn't in the build context, so the commit comes in as a build arg (build.ps1 passes it) and ends up in
# InformationalVersion as "<version>+<sha>", shown on the Settings page.
ARG GIT_SHA=
# No --no-restore: the SDK decides some implicit package references (e.g. the package that ships
# _framework/blazor.web.js as a static web asset) from the project's .razor files, which weren't present
# during the csproj-only restore above. Re-restoring here is fast (packages are already in the layer cache).
RUN dotnet publish src/PodcastToYoto.Web/PodcastToYoto.Web.csproj \
        -c Release -o /app /p:UseAppHost=false /p:SourceRevisionId="$GIT_SHA"

# ---- Runtime -------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# /data holds the SQLite DB, Data Protection keys and temp downloads. Owned by the non-root `app` user
# (UID $APP_UID, 1654) so named volumes inherit the right ownership; bind mounts must be chowned on the host.
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data

COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    App__DataDirectory=/data
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "PodcastToYoto.Web.dll"]
