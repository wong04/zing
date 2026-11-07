# Build context is the server/ directory: docker build -t zing-server server
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Zing.Server/Zing.Server.csproj Zing.Server/
RUN dotnet restore Zing.Server
COPY Zing.Server/ Zing.Server/
RUN dotnet publish Zing.Server -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# The database lives on a mounted volume at /data (ZING_DB_PATH).
RUN mkdir -p /data && chown app:app /data
USER app
CMD ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet Zing.Server.dll"]
