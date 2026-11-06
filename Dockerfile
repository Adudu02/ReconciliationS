# One image definition, several targets: api, worker, migrator and sat-stub (design D1).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig Reconciliation.slnx ./
COPY src ./src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Reconciliation.Web -c Release -o /out/web \
 && dotnet publish src/Reconciliation.Api -c Release -o /out/api \
 && dotnet publish src/Reconciliation.Worker -c Release -o /out/worker \
 && dotnet publish src/Reconciliation.Migrator -c Release -o /out/migrator \
 && dotnet publish src/Reconciliation.SatStub -c Release -o /out/sat-stub

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS api
WORKDIR /app
COPY --from=build /out/api ./
COPY --from=build /out/web/wwwroot ./wwwroot
RUN mkdir -p .local/sat-recordings && chown -R $APP_UID .local
ENV ASPNETCORE_URLS=http://+:3000
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconciliation.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS worker
WORKDIR /app
COPY --from=build /out/worker ./
RUN mkdir -p .local/sat-recordings && chown -R $APP_UID .local
ENV ASPNETCORE_URLS=http://+:3001
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconciliation.Worker.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS migrator
WORKDIR /app
COPY --from=build /out/migrator ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconciliation.Migrator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS sat-stub
WORKDIR /app
COPY --from=build /out/sat-stub ./
# SOAP and local control stay on the Compose network; no host-published control port.
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconciliation.SatStub.dll"]
