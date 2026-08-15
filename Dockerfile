# syntax=docker/dockerfile:1

# Runnable image of the "Library" OData V4 test server, ASP.NET Core implementation.
#
# Consumers start this image, point their client at http://<host>:<port>/odata/v4/library and get a
# server with fixed, well-known seed data.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so a source change does not invalidate the restore layer.
COPY src/LibraryService/LibraryService.csproj src/LibraryService/
RUN dotnet restore src/LibraryService/LibraryService.csproj

COPY . .
RUN dotnet publish src/LibraryService/LibraryService.csproj -c Release -o /app --no-restore

# One image, one container, one port - the contract consumers depend on. The database is a real Postgres,
# and it runs in here next to the service rather than in a second container, so that starting this server
# stays `docker run -p 4004:4004 …` with nothing to compose, mount or wait for.
#
# It is still throwaway: the data directory is built fresh on every start and lives only as long as the
# container, so a restart is a reset and every start is the identical, well-known state.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Postgres 18 from PGDG, not the 16 Ubuntu 24.04 ships: it has to be the same major version as
# DatabaseInit.PostgresImage, which is what a local `dotnet run` starts. A server whose behaviour depended
# on how it was started would defeat the purpose of the exercise.
#
# curl is for the healthcheck below, which the runtime image does not carry either.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
         -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc]" \
         "https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
         > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-18 postgresql-client-18 \
    && rm -rf /var/lib/apt/lists/*

ENV PATH="/usr/lib/postgresql/18/bin:${PATH}"

COPY --from=build /app .

# The same two scripts a local run applies. Postgres executes them itself, in name order, before the
# service is started - see docker-entrypoint.sh.
COPY db/ /docker-entrypoint-initdb.d/
COPY docker-entrypoint.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_HTTP_PORTS=4004
EXPOSE 4004

# Against the service, not the runtime: `dotnet --info` would report healthy while the database was still
# initialising and every request was failing.
HEALTHCHECK --interval=5s --timeout=3s --start-period=30s --retries=10 \
  CMD curl -fsS http://localhost:4004/odata/v4/library/ || exit 1

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
