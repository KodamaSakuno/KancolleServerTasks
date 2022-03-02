FROM mcr.microsoft.com/dotnet/runtime-deps:6.0 AS base
RUN apt-get update && apt-get -y install cron xorg openbox libnss3 libasound2 && rm -rf /var/lib/apt/lists/* && touch /var/log/cron.log

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS restore
WORKDIR /src
COPY DmmLogin.csproj .
RUN dotnet restore "DmmLogin.csproj" -r linux-x64

FROM restore AS playwright
COPY ./install-browser.sh ./
RUN chmod +x install-browser.sh && ./install-browser.sh

FROM restore AS publish
COPY Program.cs .
RUN dotnet publish "DmmLogin.csproj" -c Release -r linux-x64 --self-contained true -o /app/publish

FROM base AS final
COPY ./execute-cron /etc/cron.d/execute-cron
RUN chmod 0644 /etc/cron.d/execute-cron && crontab /etc/cron.d/execute-cron

WORKDIR /app
COPY ./entrypoint.sh ./start.sh ./
RUN chmod +x entrypoint.sh start.sh
COPY --from=playwright /root/.cache/ms-playwright /root/.cache/ms-playwright
COPY --from=publish /app/publish .

ENTRYPOINT ./entrypoint.sh
