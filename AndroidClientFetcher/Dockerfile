FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY AndroidClientFetcher.csproj .
RUN dotnet restore "AndroidClientFetcher.csproj" -r linux-x64

FROM build AS publish
COPY . .
RUN dotnet publish "AndroidClientFetcher.csproj" -c Release -r linux-x64 -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "AndroidClientFetcher.dll"]