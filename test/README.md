# Request collection

Every scenario the service is meant to answer, as `.http` scripts - one file per category, each request
annotated with the status code and behaviour **actually observed** against the running server. They are
the executable counterpart to [`FEATURE-COVERAGE.md`](../FEATURE-COVERAGE.md), which carries the
reasoning.

Run them with any `.http` client (VS Code REST Client, IntelliJ HTTP Client, `httpyac`). Start the
service first:

```bash
dotnet run --project src/LibraryService
```

`@host` is set to `http://localhost:5091/odata/v4/library` (the `dotnet run` port); the container image
listens on `4004`.

| File                                       | Contents                                                                     |
| ------------------------------------------ | ---------------------------------------------------------------------------- |
| [`service.http`](service.http)             | Service document and `$metadata`                                              |
| [`read.http`](read.http)                   | Entity sets, keys, type casts, alternate key, navigation, containment         |
| [`query-options.http`](query-options.http) | `$filter`, `$orderby`, `$select`, `$expand`, `$search`, `$apply`, `$compute`, `POST …/$query` |
| [`crud.http`](crud.http)                   | Create, update, delete, deep insert, delta payloads, `@odata.bind`, cascades  |
| [`operations.http`](operations.http)       | All 13 functions (15 declarations - two are overloaded) and 14 actions         |
| [`streams.http`](streams.http)             | Media entities, the contained chapter, the `Sample` stream property           |
| [`refs.http`](refs.http)                   | `$ref` in both cardinalities                                                  |
| [`batch.http`](batch.http)                 | `$batch` (JSON)                                                               |
| [`limitations.http`](limitations.http)     | The requests that do **not** answer as the spec suggests, by cause            |

`read.http`, `query-options.http` and `service.http` are read-only. The rest change the store and assume
the freshly seeded state, so run them top to bottom and restart the service for a clean slate - the
database is held in memory, so a restart is a reset.
