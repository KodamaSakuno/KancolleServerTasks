FROM mcr.microsoft.com/dotnet/sdk:6.0 AS restore
WORKDIR /src
COPY ServerAPI.csproj .
RUN dotnet restore "ServerAPI.csproj" -r linux-x64

FROM restore AS publish
COPY . .
RUN dotnet publish "ServerAPI.csproj" -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ServerAPI.dll"]
