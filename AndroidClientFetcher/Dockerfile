FROM mcr.microsoft.com/dotnet/sdk:6.0 AS restore
WORKDIR /src
COPY AndroidClientFetcher.csproj .
RUN dotnet restore "AndroidClientFetcher.csproj" -r linux-x64

FROM restore AS publish
COPY . .
RUN dotnet publish "AndroidClientFetcher.csproj" -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "AndroidClientFetcher.dll"]