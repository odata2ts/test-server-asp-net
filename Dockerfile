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

# The runtime image carries no SDK and no database server: SQLite is a library, and the database lives in
# the process's memory. The schema is created and seeded at startup from LibrarySeed, so there is nothing
# to mount or migrate and every container starts from the identical, well-known state.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=4004
EXPOSE 4004

HEALTHCHECK --interval=5s --timeout=3s --start-period=15s --retries=10 \
  CMD ["dotnet", "--info"]

ENTRYPOINT ["dotnet", "LibraryService.dll"]
