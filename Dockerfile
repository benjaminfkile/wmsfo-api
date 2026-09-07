# docs/api.md section 18.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Wmsfo.Api && dotnet publish src/Wmsfo.Api -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -fsSL https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem -o /usr/local/share/ca-certificates/rds-global.crt \
 && update-ca-certificates && apt-get purge -y curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY templates ./templates
COPY contracts ./contracts
COPY icons ./icons
USER app
EXPOSE 5000
ENTRYPOINT ["dotnet", "Wmsfo.Api.dll"]
