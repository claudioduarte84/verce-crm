FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Verce.Api/Verce.Api.csproj
RUN dotnet publish src/Verce.Api/Verce.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/verce/brand-assets && chown -R $APP_UID:$APP_UID /var/lib/verce
USER $APP_UID
ENTRYPOINT ["dotnet", "Verce.Api.dll"]
