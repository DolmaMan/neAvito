FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NeAvito/NeAvito.csproj NeAvito/
RUN dotnet restore NeAvito/NeAvito.csproj
COPY NeAvito/ NeAvito/
RUN dotnet publish NeAvito/NeAvito.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NeAvito.dll"]
