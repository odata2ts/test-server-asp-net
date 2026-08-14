# ASP.NET Core OData and the "Library" OData V4 test model

How far ASP.NET Core OData reproduces
[`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml), and
where it cannot.

Measured against **.NET 10.0.10** (the current LTS) with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0) and
**Microsoft.EntityFrameworkCore.Sqlite 10.0.11**. Every statement below was verified against the emitted
`$metadata` and against the running service - by diffing the metadata mechanically, not by reading it.
Claims about what the *libraries* can or cannot do were checked against their actual API surface by
reflection and against isolated probe models, not inferred from this service's behaviour. The requests
behind the tables are in [`test/`](test/).

The reference model is a deliberately feature-dense probe of the OData spec, not a benchmark. A server does
not have to implement all of OData. So the tables separate three causes: what **the library** cannot
express, what **the persistence layer** costs (SQLite in memory, through EF Core), and what **this
implementation** does not do. *How* the ticked features are built is in
[IMPLEMENTATION.md](IMPLEMENTATION.md).

## Legend

| Column              | Meaning                                                                                                                 |
|---------------------|-------------------------------------------------------------------------------------------------------------------------|
| **result**          | ✅ fully supported · ⚠️ partially supported · ❌ not supported                                                          |
| **out-of-the-box**  | ✔ works with the stock library, at most a configuration call                                                           |
| **impl**            | ✔ needed custom code or a workaround here - a replacement binder, middleware, a hand-written parser, an explicit route |

Both boxes can be ticked at once: the feature exists in the library, but something about this model or the
database made it not enough on its own.

## Summary

The **protocol and operation surface is reproduced completely**: all 20 entity types, 9 complex types,
2 enums, 88 properties, 12 navigation properties, 10 entity sets, the singleton and all 29 operations -
including both overload pairs, which is the part most implementations lose. Every query option the
reference model reaches translates to SQL.

What is **not** covered comes down to five things, none of them this implementation's choice: two CSDL
attributes the model builder has no API for (`TypeDefinition`, `Unicode`), the date-part functions over
`Edm.DateTimeOffset`/`Edm.Duration` and the `geo.*` functions (both the storage layer's price), `$filter`
and `$orderby` over an open type's dynamic properties (likewise), and the `If-Match` precondition, which no
part of the stack enforces.

## Model and metadata

| Feature                                             | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| Entity types, three-level inheritance, abstract types | ✅ | ✔ |   | `Medium` → `PrintMedium` → `Magazine` → `TradeJournal`, abstract flags intact |
| Keys, composite keys                                | ✅ | ✔ |   | all keys caller-assigned |
| Complex types incl. abstract base                   | ✅ | ✔ |   |       |
| Enums incl. flags and non-ASCII members             | ✅ | ✔ | ✔ | need an explicit `EnumType<T>()` registration to survive the namespace fix-up |
| Four schemas / namespaces                           | ✅ | ✔ | ✔ | `Namespace` collapses every type into one; put back from the CLR namespaces |
| Navigation properties, referential constraints, `OnDelete` | ✅ | ✔ |   | cascade is real, not just declared |
| `Partner` on both sides (6 attributes)              | ✅ | ✔ |   | not inferred by convention; the three-argument `HasRequired`/`HasOptional` overload sets it |
| Containment                                         | ✅ | ✔ |   | addressable through the type cast, as the spec requires |
| Media entities and stream properties (`HasStream`)  | ✅ | ✔ |   | serving them is a separate row below |
| Open type, `Edm.Untyped`                            | ✅ | ✔ |   | dynamic properties round-trip, `@odata.type` annotated in the payload |
| Operations: 29 declarations (15 functions, 14 actions) | ✅ | ✔ | ✔ | 13 function names, two of them overloaded - both pairs survive; one action serves the two `Search` overloads |
| Binding parameter names                             | ✅ | ✔ |   | `SetBindingParameter` - the default name breaks `EntitySetPath` |
| Alternate key (`Core.AlternateKeys`), addressable   | ✅ | ✔ | ✔ | explicit route template; `Alias` forced by an ODL defect |
| Spatial properties (`SRID` facet)                   | ⚠️ |   |   | 6 occurrences dropped - but each carries the CSDL default, and ODL drops those from a hand-built model too |
| `TypeDefinition` (`Library.Catalog.ISBN`)           | ❌ |   |   | no such concept in the builder; emitted as plain `Edm.String`, `MaxLength` kept |
| `Unicode="false"` (`Copy/Location_`)                | ❌ |   |   | no `IsUnicode` on any property configuration |

`TypeDefinition` and `Unicode` are limits of `Microsoft.OData.ModelBuilder` 2.0.0, and the layer below can
do both: `EdmTypeDefinition` + `EdmTypeDefinitionReference` emit a `<TypeDefinition>` element,
`EdmStringTypeReference(…, isUnicode: false)` emits `Unicode="false"`, and `AddRouteComponents` takes any
`IEdmModel`. So the wall is the fluent builder, not the stack - at the price of hand-building the model.

`SRID` looks like a loss in the diff and is not: CSDL defines the default as 4326 for `Geography` and 0 for
`Geometry` - exactly the two values the reference model spells out - so a client reading only the metadata
resolves the same effective SRID either way. The payload is right regardless: `Branch.Location` serialises
as GeoJSON including `"crs": {"name": "EPSG:4326"}`.

## Protocol surface

| Feature                                             | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| Service document, `$metadata`                       | ✅ | ✔ |   |       |
| CRUD on entity sets                                 | ✅ | ✔ |   | 201 / 204 / 204 |
| Addressing by composite key                         | ✅ | ✔ |   | duplicate key refused with 409 |
| `POST` to a type with a constrained navigation (`/Copies`) | ✅ |   | ✔ | the deserializer rejects such a body outright; the payload is parsed by hand |
| `PATCH` on a set whose declared type is abstract    | ✅ | ✔ | ✔ | `@odata.type` required by the spec; without it the library hands the action a null delta and no error - the 400 is ours |
| Singleton                                           | ✅ | ✔ |   |       |
| Type-cast segments                                  | ✅ | ✔ |   |       |
| Media entity streams, all three positions           | ✅ |   | ✔ | entity content, contained entity, stream property; empty content answers 204, not 404 |
| `$ref`, both cardinalities                          | ✅ |   | ✔ | verbs differ per cardinality as the spec requires |
| Deep insert                                         | ✅ | ✔ | ✔ | the library leaves the children keyless and outside their own set; the controller registers them |
| `@odata.bind` and `{"@id": …}`, incl. binding to null | ✅ |   | ✔ | routing a binding through `Delta<T>` corrupts the store - read from the raw body instead |
| Delta payloads (OData 4.01)                         | ✅ | ✔ |   | update, removal and upsert in one request, delta response |
| `$batch` (JSON)                                     | ✅ | ✔ |   | each sub-request its own unit of work |
| Query options in the body (`POST <resource>/$query`) | ✅ | ✔ |   | `UseODataQueryRequest()` must sit *before* `UseRouting()`, else 405 |
| Error when a query option fails to translate        | ⚠️ |   | ✔ | the library streams a 200 and truncates the body mid-payload; a buffering middleware turns it into an honest 500 |
| `If-Match` / 412 precondition                       | ❌ |   |   | `@odata.etag` is emitted and the database enforces the token on `UPDATE`, but no layer reads the request header |

## Query options

Every option below is translated to SQL by EF Core, not applied in memory.

| Feature                                             | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| `$filter` - comparisons, `and`/`or`/`not`, string functions, `isof`, arithmetic | ✅ | ✔ |   |       |
| `$filter` on complex properties, `any`/`all`, `Keywords/$count` | ✅ | ✔ |   |       |
| `$filter` on enums (`eq`, flags `has`)              | ✅ | ✔ |   |       |
| `$filter` with `null`                               | ✅ | ✔ |   | conformant over a query provider; over a `List<T>` the library applies three-valued logic, which OData does not specify for `ne` or a negated comparison |
| `$filter` on `Edm.Date` / `Edm.TimeOfDay`           | ✅ |   | ✔ | the stock binder rebuilds both operands as a number - unindexable for dates, untranslatable for times (500); a replacement filter binder restates it as one comparison |
| `$orderby`, incl. across a navigation property      | ✅ | ✔ |   |       |
| `$top`, `$skip`, `$count`                           | ✅ | ✔ |   |       |
| `$select`, `$expand`, nested `$expand` options      | ✅ | ✔ |   |       |
| `$compute`                                          | ✅ | ✔ |   |       |
| `$apply` (`groupby`, `aggregate`)                   | ✅ | ✔ |   |       |
| `$search`                                           | ✅ |   | ✔ | without an `ISearchBinder` it is accepted and **silently ignored**; the binder only takes effect in the per-route container |
| `$id`, alternate key in `$filter`                   | ✅ | ✔ |   | needs `AlternateKeysODataUriResolver` in the per-route container |
| Date-part functions over `Edm.DateTimeOffset` / `Edm.Duration` (`hour()`, `year()`, `now()`) | ❌ |   |   | stored as ticks, and no SQL pulls an hour back out of one - 500. `date()`, comparison and `$orderby` work; `Edm.Date` is unaffected |
| `geo.*` functions                                   | ❌ |   |   | spatial values are stored as WKT; EF Core's spatial support is NetTopologySuite-only and does not speak `Microsoft.Spatial` at all |
| `$filter` / `$orderby` on an open type's dynamic properties | ❌ |   |   | stored as JSON |

## Vocabulary annotations

All seven the reference model declares are emitted. What none of them do is change what the runtime
accepts - that is the library's position rather than this server's.

| Term                                                | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| `Core.AlternateKeys`                                | ✅ | ✔ | ✔ | the only one that is also *effective*: the key is addressable |
| `Capabilities.SearchRestrictions`                   | ✅ | ✔ |   | emitted on `Container/Media` as in the reference model |
| `Core.OptimisticConcurrency`                        | ⚠️ | ✔ |   | emitted, `@odata.etag` in the payload, enforced by the database on `UPDATE` - but `If-Match` is never read |
| `Core.Computed` (`Medium/PopularityScore`)          | ⚠️ | ✔ |   | emitted, never enforced: a `PATCH` setting it answers 204 and the value is applied |
| `Core.Immutable` (`Loan/LoanedAt`)                  | ⚠️ | ✔ |   | same - the value is neither dropped nor rejected, and the client is told nothing |
| `Core.ComputedDefaultValue` (`Member/ActiveSince`)  | ⚠️ | ✔ |   | same |
| `Core.Permissions` (`Member/Balance`)               | ⚠️ | ✔ |   | emitted in the wrong shape - see below |

The four managed-property terms are left unenforced deliberately: they describe an intent to a client that
reads `$metadata`, and what this document is for is recording that the runtime does not share it.

**`Core.Permissions` comes out in the wrong shape.** The term is typed `Core.Permission` - an enum - so its
value belongs on the annotation itself (`<Annotation Term="Core.Permissions"
EnumMember="Core.Permission/Read"/>`, which is what CAP emits). The model builder wraps it in a record with
a property that repeats the term's name, because every `VocabularyTermConfiguration` in the library builds a
record - right for the terms whose type *is* a record, wrong for a primitive or an enum. The consequence is
not cosmetic: a generated client cannot act on the term at all - odata2ts ignores it, because the
annotation holds a `Record` rather than one of the constant forms it evaluates. The other three terms are
tags, whose `Bool` the builder does put in the right place.

## Deviations from the reference EDMX

Two, both forced from below rather than chosen:

- the alternate key's `PropertyRef` carries an `Alias`, because `Microsoft.OData.Edm` throws a
  `NullReferenceException` at startup without one
- addressing by that key needs the type cast (`/Media/Library.Catalog.PrintMedium(ISBN='…')`), because
  `ISBN` is declared on `PrintMedium` while the entity set is of `Medium` - which is what the spec asks
  for, and the same shape containment already has here

Beyond that, everything the reference model declares is emitted except `TypeDefinition`, `Unicode` and the
six redundant `SRID` facets.
