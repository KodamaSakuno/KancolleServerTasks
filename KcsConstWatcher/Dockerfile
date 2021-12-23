FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
RUN apt-get update && apt-get -y install cron && rm -rf /var/lib/apt/lists/* && touch /var/log/cron.log

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY KcsConstWatcher.csproj .
RUN dotnet restore "KcsConstWatcher.csproj" -r linux-x64

FROM build AS publish
COPY Program.cs .
RUN dotnet publish "KcsConstWatcher.csproj" -c Release -r linux-x64 --self-contained false -o /app/publish

FROM base AS final
COPY ./execute-cron /etc/cron.d/execute-cron
RUN chmod 0644 /etc/cron.d/execute-cron && crontab /etc/cron.d/execute-cron

WORKDIR /app
COPY ./entrypoint.sh ./start.sh ./
RUN chmod +x entrypoint.sh start.sh
COPY --from=publish /app/publish .

ENTRYPOINT ./entrypoint.sh