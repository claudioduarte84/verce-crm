FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Verce.Api/Verce.Api.csproj
RUN dotnet publish src/Verce.Api/Verce.Api.csproj -c Release --no-restore -o /app

# ADR-0007 §6 / SECURITY §8: real, sandboxed, headless Chromium for HTML->PDF rendering
# (QuotePdfService -> BlockTreeRenderer -> Microsoft.Playwright). Microsoft's own Playwright .NET
# image is pinned to the EXACT SAME version as the `Microsoft.Playwright` NuGet package referenced
# by Verce.Modules.Documents.csproj (v1.62.0) — the only way to guarantee the Chromium build this
# image ships is the one the driver actually expects, instead of hand-rolling the long, fragile
# apt-get dependency list (libnss3, libatk, libgbm, fonts-liberation, ...) Chromium headless needs
# and risking silent drift from the pinned NuGet version at the next rebuild. It already bundles a
# matching ASP.NET Core 10 runtime, so it replaces mcr.microsoft.com/dotnet/aspnet:10.0 outright
# rather than layering on top of it.
FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble AS runtime
WORKDIR /app
COPY --from=build /app .
# Documents storage: a dedicated, persistent, non-/tmp path (see docker-compose.yml's
# verce_documents volume and docs/OPERATIONS.md §4 backup coverage) — generated PDF/HTML
# artifacts are evidence (CLAUDE.md rule 13) and must survive a container recreate exactly like
# brand assets already do.
# /app itself must be owned by the non-root app user too: `dotnet publish`'s own bundled
# Playwright Node.js driver (.playwright/node/linux-x64/node, copied in by the publish step
# above) is copied in owned by root with no guarantee the execute bit survives the copy, and
# PlaywrightHtmlToPdfRenderer.StartAsync spawns it directly — without this it fails closed at
# startup with Win32Exception "Permission denied", never merely a degraded health state.
RUN mkdir -p /var/lib/verce/brand-assets /var/lib/verce/documents \
    && chown -R $APP_UID:$APP_UID /var/lib/verce /app \
    && chmod +x /app/.playwright/node/linux-x64/node
USER $APP_UID
ENTRYPOINT ["dotnet", "Verce.Api.dll"]
