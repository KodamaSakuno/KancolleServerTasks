FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS restore
WORKDIR /src
COPY DmmLogin.csproj .
RUN dotnet restore "DmmLogin.csproj" -r linux-musl-x64

FROM restore AS publish
COPY Program.cs .
RUN dotnet publish "DmmLogin.csproj" --no-restore -c Release -r linux-musl-x64 --self-contained true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine AS final
RUN addgroup --gid 1000 appgroup && \
    adduser --uid 1000 --ingroup appgroup --home /app --disabled-password appuser && \
    touch /var/log/cron.log && \
    chmod 766 /var/log/cron.log

COPY ./entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod 755 /usr/local/bin/entrypoint.sh && \
    echo | crontab - && \
    echo "0 */2 * * * /app/start.sh" | crontab -u appuser -

WORKDIR /app
COPY --chown=appuser:appgroup ./start.sh .
RUN chmod +x start.sh
COPY --from=publish --chown=appuser:appgroup /app/publish .

ENTRYPOINT ["entrypoint.sh"]
