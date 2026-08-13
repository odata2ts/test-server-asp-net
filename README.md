# test-server-asp-net

An **ASP.NET Core** implementation of the odata2ts **"Library"** OData V4 server feature test model - a
deliberately feature-dense model used to evaluate which OData spec features a given server implementation
actually supports.

C# / .NET, backed by SQLite held in memory through EF Core. No external services, nothing to install.

## Why this exists

The reference model lives in its own repository,
[odata2ts/test-reference-model](https://github.com/odata2ts/test-reference-model):

- [`model/library.md`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.md) - concept, design decisions, feature → location mapping
- [`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml) - the reference EDMX (OData **4.01**, 100 % CSDL-conformant)

That model is a probe of the OData spec, not a benchmark. A server does not have to implement all of
OData, and a framework may well solve a modelling problem its own way. So this repo asks two questions:

1. How much of the model can ASP.NET Core OData express?
2. Where it does something else - **is that a gap, or a different design?**

The answer is in **[FEATURE-COVERAGE.md](FEATURE-COVERAGE.md)**, the actual deliverable. It is based on
the emitted `$metadata`, diffed mechanically against the reference EDMX, and on requests against the
running service - not on documentation.

Short version: the protocol and operation surface is reproduced completely - all 20 entity types, all 29
operations including both overload pairs, containment, media entities, open types, `$batch`, `$apply` and
the query options in the request body (`POST <resource>/$query`).
Media entity streams, `$ref`, deep insert and 4.01 delta payloads are all served, and so is the model
metadata down to `Partner` on both sides of every association, alternate keys and all four vocabulary
annotations. Two attributes of the reference EDMX have no equivalent in the model builder at all:
`TypeDefinition` and `Unicode`. `SRID` is dropped too, but carries the CSDL default value throughout the
reference model and therefore costs nothing.

Because the store is a real database, the query options are translated to SQL rather than run over
`List<T>` - which is what a consumer of a real service meets, and a third thing the report separates out:
*what the persistence layer costs*. Four types need a value converter to survive a SQLite column at all,
`Edm.Date` and `Edm.TimeOfDay` need a replacement filter binder to be compared as values rather than as
arithmetic on their parts, and the date-part functions over `Edm.DateTimeOffset` are what that costs.

## Version policy

**Always the current .NET LTS.** Today that is **.NET 10** (active until 2028-11-14).

The OData library is `Microsoft.AspNetCore.OData` **9.5.0** - the latest *stable* release. It targets
`net8.0`; a `net10.0` build exists only as a preview. Running the stable package on .NET 10 was verified
to build and to serve correct `$metadata` before it was adopted. The LTS rule governs the platform; a
preview NuGet in a reference test server would be the wrong place to take a risk.

`Microsoft.EntityFrameworkCore.Sqlite` is **10.0.11**, which ships with the same LTS and needs no separate
policy.

## Getting started

### As a container

The published image is the intended way to consume this server:

```bash
docker run --rm -p 4004:4004 ghcr.io/odata2ts/test-server-asp-net:latest
```

The database lives in memory and is created and seeded at startup, so every container starts from the
identical, well-known state - which is what makes it usable from an automated test suite. There is nothing
to mount, migrate or wait for, and a restart is a reset. `latest` is republished from every push to `main`, and a
version tag additionally yields `1.2.3`, `1.2` and `1`. The image is smoke-tested before it is pushed.

### Locally

Requires the .NET 10 SDK.

```bash
dotnet run --project src/LibraryService
```

Service root: <http://localhost:5000/odata/v4/library/> ·
metadata: <http://localhost:5000/odata/v4/library/$metadata>

## Layout

| Path                                  | Contents                                                              |
| ------------------------------------- | --------------------------------------------------------------------- |
| `src/LibraryService/Model/`           | The CLR types, one file per schema of the reference model              |
| `src/LibraryService/EdmModelBuilder.cs` | The EDM model, built explicitly wherever a convention would not do   |
| `src/LibraryService/Data/`            | The `DbContext`, the seed with its fixed keys, and the value converters |
| `src/LibraryService/Controllers/`     | Entity sets and singleton; all functions and actions                   |
| `src/LibraryService/Query/`           | `$search` binder - without it the option is silently ignored - and the replacement filter binder |

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
- The SQLite connection is opened once in `Program.cs` and **never closed**: an in-memory database lives
  exactly as long as a connection to it is open.
- **Keys are caller-assigned everywhere**, never generated by the database. The change tracker therefore
  cannot recognise a new entity by its key: a new entity reached only through a tracked entity's
  navigation property is taken for an existing row and written with an `UPDATE` that matches nothing. Add
  it to its own `DbSet` first, then link it.
- Navigation properties are **not** populated by loading their parent - there is no lazy loading. A
  missing `Include` reads as an empty collection or a null link, not as an error.

## Conventions

Aligned with the other odata2ts repositories: EditorConfig (LF, UTF-8, 2 spaces, 4 for C#), Conventional
Commits with squash-merged PRs whose **title** is itself a valid commit message, MIT licensed.
