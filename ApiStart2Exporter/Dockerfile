FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS restore
WORKDIR /src
COPY ApiStart2Exporter.csproj .
RUN dotnet restore "ApiStart2Exporter.csproj" -r linux-musl-x64

FROM restore AS publish
COPY . .
RUN dotnet publish "ApiStart2Exporter.csproj" --no-restore -c Release -r linux-musl-x64 --self-contained true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine
RUN addgroup --gid 1000 appgroup && \
    adduser --uid 1000 --ingroup appgroup --home /app --disabled-password appuser

WORKDIR /app
COPY --from=publish --chown=appuser:appgroup /app/publish .

USER appuser
ENTRYPOINT ["./ApiStart2Exporter"]
