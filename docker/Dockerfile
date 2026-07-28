# Imagen todo-en-uno: PostgreSQL 17 + API .NET 10 + front React servido como estático.
# Un solo contenedor, un solo puerto (8080).
#
# El PGDATA vive en /var/lib/postgresql/data y DEBE montarse en un volumen nombrado.
# Sin volumen, el primer `docker rm` se lleva la base puesta.

# ---------- 1. Front ----------
FROM node:24-bookworm-slim AS web
WORKDIR /web

COPY src/Ways.Web/package.json src/Ways.Web/package-lock.json ./
RUN npm ci

COPY src/Ways.Web/ ./
RUN npm run build

# ---------- 2. API ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src

COPY Ways.slnx ./
COPY src/Ways.Domain/Ways.Domain.csproj                 src/Ways.Domain/
COPY src/Ways.Application/Ways.Application.csproj       src/Ways.Application/
COPY src/Ways.Infrastructure/Ways.Infrastructure.csproj src/Ways.Infrastructure/
COPY src/Ways.Api/Ways.Api.csproj                       src/Ways.Api/
COPY tests/Ways.Domain.Tests/Ways.Domain.Tests.csproj             tests/Ways.Domain.Tests/
COPY tests/Ways.Application.Tests/Ways.Application.Tests.csproj   tests/Ways.Application.Tests/
RUN dotnet restore src/Ways.Api/Ways.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ways.Api/Ways.Api.csproj -c Release -o /out --no-restore

# ---------- 3. Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Instalar PostgreSQL adentro solo tiene sentido en el modo todo-en-uno.
# Con una base externa son ~270 MB y un par de minutos de build que no se usan nunca,
# así que viene apagado por defecto:  --build-arg INCLUIR_POSTGRES=true
ARG INCLUIR_POSTGRES=false

ENV DEBIAN_FRONTEND=noninteractive \
    PGDATA=/var/lib/postgresql/data \
    PGBIN=/usr/lib/postgresql/17/bin \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

# curl lo necesita el HEALTHCHECK en los dos modos.
# La imagen base es Ubuntu 24.04, que solo trae PostgreSQL 16 en sus repos,
# así que para la 17 se agrega el repositorio oficial PGDG.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    if [ "$INCLUIR_POSTGRES" = "true" ]; then \
        apt-get install -y --no-install-recommends gnupg; \
        install -d -m 0755 /usr/share/postgresql-common/pgdg; \
        curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
            -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc; \
        echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] \
https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
            > /etc/apt/sources.list.d/pgdg.list; \
        apt-get update; \
        apt-get install -y --no-install-recommends postgresql-17 postgresql-contrib-17; \
        apt-get purge -y gnupg; \
        apt-get autoremove -y; \
        mkdir -p /var/lib/postgresql/data /var/log/postgresql; \
        chown -R postgres:postgres /var/lib/postgresql /var/log/postgresql; \
    fi; \
    rm -rf /var/lib/apt/lists/*

COPY --from=api /out      /app
COPY --from=web /web/dist /app/wwwroot

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

WORKDIR /app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/salud || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
