FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS restore
WORKDIR /src
COPY AndroidClientExporter.csproj .
RUN dotnet restore "AndroidClientExporter.csproj" -r linux-musl-x64

FROM restore AS publish
COPY . .
RUN dotnet publish "AndroidClientExporter.csproj" --no-restore -c Release -r linux-musl-x64 --self-contained true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:6.0-alpine
RUN addgroup --gid 1000 appgroup && \
    adduser --uid 1000 --ingroup appgroup --home /app --disabled-password appuser

ARG DOCKER_GID
RUN addgroup --gid $DOCKER_GID docker && \
    apk --no-cache add shadow && \
    usermod -aG docker appuser

WORKDIR /app
COPY --from=publish --chown=appuser:appgroup /app/publish .

USER appuser
ENTRYPOINT ["./AndroidClientExporter"]
