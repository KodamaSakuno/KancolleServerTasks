FROM mcr.microsoft.com/dotnet/sdk:6.0 AS restore
WORKDIR /src
COPY AndroidAbyssalShipCGFetcher.csproj .
RUN dotnet restore "AndroidAbyssalShipCGFetcher.csproj" -r linux-x64

FROM restore AS publish
COPY . .
RUN dotnet publish "AndroidAbyssalShipCGFetcher.csproj" -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0
RUN groupadd --gid 1000 appuser && \
    useradd --uid 1000 --gid 1000 --shell /bin/bash --home-dir /app appuser

WORKDIR /app
COPY --from=publish --chown=1000:1000 /app/publish .

USER appuser
ENTRYPOINT ["./AndroidAbyssalShipCGFetcher"]
