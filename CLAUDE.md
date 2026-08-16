# test-server-asp-net

ASP.NET Core OData implementation of the odata2ts "Library" reference model
([odata2ts/test-reference-model](https://github.com/odata2ts/test-reference-model)).
Setup and layout are in [README.md](README.md) — this file only states what governs
decisions.

## Purpose

1. **Test server for odata2ts integration tests.** Consumed as a container image by `odata2ts/int-test`.
   Behaviour must be stable and assertable: fixed keys, a fresh in-memory DB per start, no state that
   depends on insert timing or wall-clock time. A change that alters responses breaks consumers.
2. **Documentation of ASP.NET Core OData's feature coverage.** What the library can and cannot do, and
   how much of that is our own doing, is the second deliverable — see below.

## Running it

```bash
docker run --rm -p 4004:4004 ghcr.io/odata2ts/test-server-asp-net:latest
```

The published image is the intended way to consume the server: nothing to compose, mount, migrate or wait
for, a restart is a reset. It carries its own PostgreSQL, so the single-container contract holds — do not
break it by splitting the database out. `latest` is republished from every push to `main`, a version tag
additionally yields `1.2.3`, `1.2` and `1`; images are smoke-tested before they are pushed. Locally
instead (.NET 10 SDK + Docker): `dotnet run --project src/LibraryService` on
<http://localhost:5091/odata/v4/library/> — with no connection string it starts its own Postgres.

**Schema and seed are SQL under `db/`**, applied by Postgres before the service starts; the service has no
seeding code. `db/01-schema.sql` is generated — after changing the model, regenerate it with
`dotnet run --project src/LibraryService -- --emit-schema ../../db/01-schema.sql`, never edit it by hand.

## The spec is the source of truth

The OData V4 / V4.01 specification decides what is correct — HTTP status codes, payload shapes, error
responses, semantics of every query option. Not what the library happens to do, not what is convenient.
Where ASP.NET Core OData deviates, either work around it or record the deviation; never redefine
"correct" to match the implementation.

## Feature coverage is the goal

Cover as much of OData V4 and V4.01 as possible.

- Custom implementations and workarounds are explicitly allowed (replacement binders, hand-written
  parsers, middleware, a hand-built `IEdmModel` — all already in use).
- **If a feature is expensive, quite complex or more or less impossible: stop and ask the user.** Do not
  silently skip it and do not build something elaborate on your own judgement.

## Deliberately out of scope for now

- **Spatial types.** The properties exist and round-trip (stored as WKT), but nothing beyond that is
  pursued: no `geo.*` functions, no NetTopologySuite/PostGIS. EF Core's spatial support does not speak
  `Microsoft.Spatial` at all, so the cost is out of proportion. Do not start on it without asking.

## Testing

Requests live in `test/` as `.http` request scripts, following the sibling repo
[`server/cap`](../cap/test/requests.http): one collection of scenarios, `@host` and the seed's fixed keys
as variables at the top, and **every request annotated with the status code and behaviour actually
observed** against the running server. New or changed behaviour gets a request there; the file is the
executable counterpart to FEATURE-COVERAGE.md.

That annotation is a contract, not a comment: CI replays the whole collection against the image and
asserts the status code on the **first line** of every `### ` block, so a request without one fails the
build and a changed response fails it too. Run it locally with `npm test` (needs
`docker build -t test-server-asp-net:local .` first); `npm run lint:requests` checks the annotations
alone. The harness is `test/harness/` — nothing runner-specific goes into the `.http` files.

## Recording coverage

Two files, and the split between them is strict:

- [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md) — **what** is covered, as tables: one row per feature with a
  result (✅ / ⚠️ / ❌), an *out-of-the-box* and an *impl* tick, and a one-line note. Prose only where a
  verdict needs justifying. Every finding must attribute itself to one of three causes: what **the library
  / model builder** cannot express, what **the persistence layer** costs (PostgreSQL via EF Core — no
  spatial types, no query access to the open type's dynamic properties), what **this implementation**
  does not do.
- [IMPLEMENTATION.md](IMPLEMENTATION.md) — **how**: every workaround, replacement binder, middleware,
  hand-written parser and library trap. Anything code-level belongs here, not in the coverage tables; a
  row that carries an *impl* tick is explained here.

Claims are verified against the emitted `$metadata` and the running service, not inferred or assumed.
