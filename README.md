# test-server-asp-net

An **ASP.NET Core** implementation of the odata2ts **"Library"** OData V4 reference model: a live service
to run integration tests against, shipped as a container image, and a report on how much of OData V4 /
4.01 ASP.NET Core OData actually covers.

C# / .NET, backed by PostgreSQL through EF Core. The database ships inside the image, so there is still
only one container to start and nothing to install.

## Why this exists

**First: to be run.** odata2ts generates TypeScript clients from an OData service, and those clients have
to be tested against a real one. This server is published as a container image and consumed by odata2ts'
own integration tests, which start it, generate against its `$metadata` and issue requests at it:

```bash
docker run --rm -p 4004:4004 ghcr.io/odata2ts/test-server-asp-net:latest
```

The database lives inside that one container and is built from `db/*.sql` on every start, so every start is
the identical, well-known state and a restart is a reset. Nothing to compose, mount, migrate or wait for,
and no response that depends on insert order or wall-clock time - which is what makes the server assertable
from an automated suite.

**Second: to document what ASP.NET Core OData can do.** The model it serves is not an example
application, it is the odata2ts reference model, a deliberately feature-dense probe of the OData spec that
lives in its own repository, [odata2ts/test-reference-model](https://github.com/odata2ts/test-reference-model):

- [`model/library.md`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.md) - concept, design decisions, feature → location mapping
- [`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml) - the reference EDMX (OData **4.01**, 100 % CSDL-conformant)

A probe, not a benchmark: a server does not have to implement all of OData, and a framework may well solve
a modelling problem its own way. So this repo asks two questions:

1. How much of the model can ASP.NET Core OData express?
2. Where it does something else - **is that a gap, or a different design?**

The answer is in **[FEATURE-COVERAGE.md](FEATURE-COVERAGE.md)**, the second deliverable. It is based on
the emitted `$metadata`, diffed mechanically against the reference EDMX, and on requests against the
running service - not on documentation - and it separates what the library cannot express, what the
persistence layer costs and what this implementation simply does not do. How the covered features are
built - the workarounds, replacement binders and library traps behind them - is in
[IMPLEMENTATION.md](IMPLEMENTATION.md).

## Version policy

**Always the current .NET LTS.** Today that is **.NET 10** (active until 2028-11-14).

The OData library is `Microsoft.AspNetCore.OData` **9.5.0** - the latest *stable* release. It targets
`net8.0`; a `net10.0` build exists only as a preview. Running the stable package on .NET 10 was verified
to build and to serve correct `$metadata` before it was adopted. The LTS rule governs the platform; a
preview NuGet in a reference test server would be the wrong place to take a risk.

`Npgsql.EntityFrameworkCore.PostgreSQL` is **10.0.3**, which tracks the same LTS and needs no separate
policy. The database is **PostgreSQL 18**, the current stable major, and the version is pinned in two
places that have to agree: the `postgresql-18` packages in the `Dockerfile` and
`DatabaseInit.PostgresImage`, which is the image a local run starts. Ubuntu 24.04 ships 16, so the image
takes 18 from PGDG rather than from the distribution.

## Getting started

### As a container

The published image is the intended way to consume this server:

```bash
docker run --rm -p 4004:4004 ghcr.io/odata2ts/test-server-asp-net:latest
```

`latest` is republished from every push to `main`, and a version tag additionally yields `1.2.3`, `1.2` and
`1`. The image is smoke-tested before it is pushed.

### Locally

Requires the .NET 10 SDK and a running Docker daemon.

```bash
dotnet run --project src/LibraryService
```

Still a one-liner: with no connection string configured the service starts a Postgres container itself,
applies the same `db/*.sql` and waits for it. Nothing to install or set up, and the container goes away
with the process.

Service root: <http://localhost:5091/odata/v4/library/> ·
metadata: <http://localhost:5091/odata/v4/library/$metadata>

To point it at a Postgres of your own instead, set a connection string - which is also what the published
image does:

```bash
ConnectionStrings__Library="Host=localhost;Database=library;Username=library;Password=library" \
  dotnet run --project src/LibraryService
```

### Trying it out

[`test/`](test/) holds every scenario the service is meant to answer as `.http` scripts - one file per
category, each request annotated with the status code and behaviour **actually observed**, including the
ones that do *not* answer as the spec suggests ([`limitations.http`](test/limitations.http)). They are the
executable counterpart to FEATURE-COVERAGE.md and run in any `.http` client. See
[`test/README.md`](test/README.md).

## Layout

| Path                                  | Contents                                                              |
| ------------------------------------- | --------------------------------------------------------------------- |
| `src/LibraryService/Model/`           | The CLR types, one file per schema of the reference model              |
| `src/LibraryService/EdmModelBuilder.cs` | The EDM model, built explicitly wherever a convention would not do   |
| `src/LibraryService/Data/`            | The `DbContext`, the two value converters, and how the database is brought up |
| `src/LibraryService/Controllers/`     | Entity sets and singleton; all functions and actions                   |
| `src/LibraryService/Query/`           | `$search` binder - without it the option is silently ignored - and the replacement filter binder |
| `db/`                                 | The schema (generated from the EF model) and the seed with its fixed keys, as SQL |
| `docker-entrypoint.sh`                | Starts Postgres, applies `db/*.sql`, then the service - in that order  |
| `test/`                               | The `.http` request collection, one file per category                  |

Streams (`$value`, the `Sample` stream property, contained chapters) and `$ref` live in
`Controllers/StreamControllers.cs` and `Controllers/RefControllers.cs`.

Notes worth knowing before editing:

- The **CLR namespace becomes the EDM namespace**. `Library.Catalog`, `Library.Circulation` and
  `PublisherRegistry` are load-bearing, not decoration.
- `ODataConventionModelBuilder.Namespace` names the container **and** drags every type into that
  namespace. `AlignNamespacesWithClrTypes` puts them back; a new type is covered automatically, a new
  enum is not - those need an explicit `EnumType<T>()` registration first.
- Query components such as the `$search` binder and the replacement `IFilterBinder` resolve from the
  **per-route** container, not from the application's service provider. A global registration compiles,
  runs, and is never used.
- Binding parameters are renamed to the reference model's names, because `EntitySetPath` refers to them
  by name.
- The **schema and the seed are SQL** under `db/`, applied by Postgres before the service starts - there is
  no seeding code. `db/01-schema.sql` is *generated* from the EF model, so regenerate it after changing the
  model: `dotnet run --project src/LibraryService -- --emit-schema ../../db/01-schema.sql`.
- **Timestamps are UTC only.** A non-zero offset is answered with 400, deliberately - see
  [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md).
- **Keys are caller-assigned everywhere**, never generated by the database. The change tracker therefore
  cannot recognise a new entity by its key: a new entity reached only through a tracked entity's
  navigation property is taken for an existing row and written with an `UPDATE` that matches nothing. Add
  it to its own `DbSet` first, then link it.
- Navigation properties are **not** populated by loading their parent - there is no lazy loading. A
  missing `Include` reads as an empty collection or a null link, not as an error.

Each of these, and every other workaround this model needed, is written up in
[IMPLEMENTATION.md](IMPLEMENTATION.md).

## Conventions

Aligned with the other odata2ts repositories: EditorConfig (LF, UTF-8, 2 spaces, 4 for C#), Conventional
Commits with squash-merged PRs whose **title** is itself a valid commit message, MIT licensed.
