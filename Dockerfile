FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Clawindex.Collector.slnx ./
COPY src/Clawindex.Collector.Api/Clawindex.Collector.Api.csproj src/Clawindex.Collector.Api/
RUN dotnet restore src/Clawindex.Collector.Api/Clawindex.Collector.Api.csproj

COPY src/Clawindex.Collector.Api src/Clawindex.Collector.Api
RUN dotnet publish src/Clawindex.Collector.Api/Clawindex.Collector.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV CLAWINDEX_DB_PATH=/data/clawindex-collector.db
ENV OTEL_EXPORTER_OTLP_PROTOCOL=grpc

VOLUME ["/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "Clawindex.Collector.Api.dll"]
