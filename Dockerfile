# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GetLaps/GetLaps.csproj src/GetLaps/
RUN dotnet restore src/GetLaps/GetLaps.csproj
COPY src/ src/
RUN dotnet publish src/GetLaps/GetLaps.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/keys
RUN mkdir -p /keys && chown app:app /keys
COPY --from=build /app/publish .
USER app
EXPOSE 8080
VOLUME ["/keys"]
ENTRYPOINT ["dotnet", "GetLaps.dll"]
