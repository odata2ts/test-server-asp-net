# ASP.NET Core OData and the "Library" OData V4 test model

This project is an ASP.NET Core OData implementation of the
[OData V4 reference model](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml) of odata2ts.
The reference model defines a data model, which tries to express as much OData V4 functionality as possible
while remaining semantically meaningful to human beings. And while a server does not have to implement all OData 
features, it's good to know which are achievable within a framework. Hence, we try to discern the following:
* which features are supported in a realistic setup?
* do you get this feature support out-of-the-box or, otherwise, how much implementation effort is it?

So the tables separate three causes: what **the library** cannot
express, what **the persistence layer** costs (PostgreSQL, through EF Core), and what **this
implementation** does not do. *How* the ticked features are built is in
[IMPLEMENTATION.md](IMPLEMENTATION.md).


How far ASP.NET Core OData reproduces
[`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml), and
where it cannot.

Measured against **.NET 10.0.10** (the current LTS) with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0) and
**Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3** against **PostgreSQL 18**. Every statement below was verified against the emitted
`$metadata` and against the running service - by diffing the metadata mechanically, not by reading it.
Claims about what the *libraries* can or cannot do were checked against their actual API surface by
reflection and against isolated probe models, not inferred from this service's behaviour. The requests
behind the tables are in [`test/`](test/).

The reference model is a deliberately feature-dense probe of the OData spec, not a benchmark. A server does
not have to implement all of OData. So the tables separate three causes: what **the library** cannot
express, what **the persistence layer** costs (PostgreSQL, through EF Core), and what **this
implementation** does not do. *How* the ticked features are built is in
[IMPLEMENTATION.md](IMPLEMENTATION.md).

The persistence layer costs very little now, and that is a recent change worth stating plainly: the store
was SQLite in memory until 2026-08-15. SQLite has no type for a decimal, a timestamp or a duration, so all
three were stored as scaled integers or tick counts, and the date-part functions over them either failed
with a 500 or - in one case - answered wrongly without saying so. Postgres has `numeric`, `timestamptz`
and `interval`, so those rows are not reworded below but gone, together with the three value converters
that produced them. What the persistence layer still costs is the spatial types and the open type's
dynamic properties, both of which have no relational form at all.

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

What is **not** covered comes down to eight things, seven of them not this implementation's choice: three
pieces of CSDL the model builder has no API for (`TypeDefinition`, `Unicode`, and every `OnDelete` action
except `Cascade`), the `geo.*` functions and `$filter`/`$orderby` over an open type's dynamic properties
(both the storage layer's price), the `If-Match` precondition, which no part of the stack enforces, and the
library's projection, which both fails to truncate a `$compute=date(...)` and breaks outright on a
date-part function over a nullable property.

Only one is this implementation's own: a contained collection answers 200 with an empty result for a
parent that does not exist, where the spec wants 404.

The eighth is a choice, and the only place this server knowingly departs from the spec: **it accepts UTC
timestamps only**. `Edm.DateTimeOffset` permits any offset, and a fully conformant server round-trips
`+02:00` unchanged; this one answers **400** with a message naming the property and the UTC value to send
instead. The reasoning is that UTC on the wire and UTC at rest is best practice - it is what the entire
reference model already does - and that a deviation should be visible: normalising the value silently would
hand the client back a timestamp it never sent, which is the worse outcome of the two. A client that
already speaks UTC never meets this.

## Model and metadata

| Feature                                             | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| Entity types, three-level inheritance, abstract types | ✅ | ✔ |   | `Medium` → `PrintMedium` → `Magazine` → `TradeJournal`, abstract flags intact |
| Keys, composite keys                                | ✅ | ✔ |   | all keys caller-assigned |
| Complex types incl. abstract base                   | ✅ | ✔ |   |       |
| Enums incl. flags and non-ASCII members             | ✅ | ✔ | ✔ | need an explicit `EnumType<T>()` registration to survive the namespace fix-up |
| Four schemas / namespaces                           | ✅ | ✔ | ✔ | `Namespace` collapses every type into one; put back from the CLR namespaces |
| Navigation properties, referential constraints, `OnDelete="Cascade"` | ✅ | ✔ | ✔ | cascade is real, not just declared; all four are taken from EF's `DeleteBehavior` |
| `OnDelete` actions other than `Cascade`             | ❌ |   |   | CSDL has `SetNull`, `SetDefault` and `None`, and this model sets null five times - but `NavigationPropertyConfiguration` exposes `CascadeOnDelete()` and nothing else, so the behaviour happens and cannot be declared |
| `Partner` on both sides (6 attributes)              | ✅ | ✔ |   | not inferred by convention; the three-argument `HasRequired`/`HasOptional` overload sets it |
| Containment                                         | ⚠️ | ✔ | ✔ | addressable through the type cast, as the spec requires - but the collection answers 200 with an empty result for a parent that does not exist, where the spec wants 404. This implementation's action ends in `?.Chapters ?? []` |
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
| Missing required action parameter answers 400       | ✅ |   | ✔ | a body carrying none of the declared parameters binds to a *null* `ODataActionParameters`; unguarded that is a 500 |
| `@odata.bind` and `{"@id": …}`, incl. binding to null | ✅ |   | ✔ | routing a binding through `Delta<T>` corrupts the store - read from the raw body instead; on a create the bound stub is `Add`ed with the graph and has to be swapped for the stored entity first |
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
| `$filter` on `Edm.Date` / `Edm.TimeOfDay`           | ✅ |   | ✔ | the stock binder rebuilds both operands as a number - correct but unindexable for dates, untranslatable for times (500); a replacement filter binder restates it as one comparison |
| `$orderby`, incl. across a navigation property      | ✅ | ✔ |   |       |
| `$top`, `$skip`, `$count`                           | ✅ | ✔ |   |       |
| `$select`, `$expand`, nested `$expand` options      | ✅ | ✔ | ✔ | a nested `$select` on a complex property (`Address($select=*)`) projects the owned entity by itself, which EF refuses to *track*; the read queries go out `AsNoTracking` |
| `$compute`                                          | ✅ | ✔ |   |       |
| `$apply` (`groupby`, `aggregate`)                   | ✅ | ✔ |   |       |
| `$search`                                           | ✅ |   | ✔ | without an `ISearchBinder` it is accepted and **silently ignored**; the binder only takes effect in the per-route container |
| `$id`, alternate key in `$filter`                   | ✅ | ✔ |   | needs `AlternateKeysODataUriResolver` in the per-route container |
| Date-part functions over `Edm.DateTimeOffset` (`hour()`, `year()`, `month()`, `now()`, `date()`) | ✅ | ✔ |   | `timestamptz`, so every part is available to SQL. Was ❌ while the column was an integer tick count |
| `$compute=date(...)` over `Edm.DateTimeOffset`      | ❌ |   |   | declares `Edm.Date` and returns the whole timestamp. The library's projection - the same function in `$filter` is correct |
| Date-part function over a *nullable* property, in a projection | ❌ |   |   | 500 *Nullable object must have a value* once a null row is materialised - the result is typed non-nullable. Needs all three: the date-part function, the nullable source, and a projection, which `$compute` always is and `$orderby` becomes as soon as `$select` is present. `$filter` never is |
| Date-part functions over `Edm.Duration` (`totalseconds()`) | ❌ |   |   | the library declares no such function: 400 *Unknown function*, so the store is never asked |
| `geo.*` functions                                   | ❌ |   |   | spatial values are stored as WKT; EF Core's spatial support is NetTopologySuite-only and does not speak `Microsoft.Spatial` at all |
| `$filter` / `$orderby` on an open type's dynamic properties | ❌ |   |   | stored as JSON - but the URI parser refuses an undeclared property with 400 before the store is ever asked |

## Vocabulary annotations

All seven the reference model declares are emitted, and **62 more of this server's own** - see *Annotations
beyond the reference model* below. What none of them do is change what the runtime accepts - that is the
library's position rather than this server's.

| Term                                                | result | out-of-the-box | impl | Notes |
| --------------------------------------------------- | :----: | :------------: | :--: | ----- |
| `Core.AlternateKeys`                                | ✅ | ✔ | ✔ | the only one that is also *effective*: the key is addressable |
| `Capabilities.SearchRestrictions`                   | ✅ | ✔ |   | emitted on `Container/Media` as in the reference model |
| `Core.OptimisticConcurrency`                        | ⚠️ |   | ✔ | emitted, `@odata.etag` in the payload, enforced by the database on `UPDATE` - but `If-Match` is never read. Derived from EF's fluent `IsConcurrencyToken()`, which the library cannot see |
| `Core.Computed` (`Medium/PopularityScore`)          | ⚠️ | ✔ |   | emitted, never enforced: a `PATCH` setting it answers 204 and the value is applied |
| `Core.Immutable` (`Loan/LoanedAt`)                  | ⚠️ | ✔ |   | same - the value is neither dropped nor rejected, and the client is told nothing |
| `Core.ComputedDefaultValue` (`Member/ActiveSince`)  | ⚠️ | ✔ |   | same |
| `Core.Permissions` (`Member/Balance`)               | ⚠️ |   | ✔ | correct shape only because the annotation is emitted directly - see below |

The four managed-property terms are left unenforced deliberately: they describe an intent to a client that
reads `$metadata`, and what this document is for is recording that the runtime does not share it.

**`Core.Permissions` is the one term the model builder cannot shape correctly.** It is typed
`Core.Permission` - an enum - so its value belongs on the annotation itself, not in a record. Every
`VocabularyTermConfiguration` in the library builds a record, which is right for the terms whose type *is*
a record and wrong for a primitive or an enum, and `HasPermissions()` therefore produced an annotation
holding a `Record` with a property repeating the term's name. That is not a matter of taste: the spec
requires an annotation's value expression to match the term's declared type, and a `Record` is allowed
only where that type is a structured type - so what the builder emits is invalid CSDL, and no client
reading `$metadata` by the spec can evaluate it. The other three managed-property terms are tags, whose
`Bool` the builder does put in the right place.

The term is now emitted directly, as `<EnumMember>Org.OData.Core.V1.Permission/Read</EnumMember>` - the
element form of the `EnumMember` constant the term's type calls for. That is what the **impl** tick on the
row is: out of the box the term comes out unusable. How the annotations are declared and translated is in
[IMPLEMENTATION.md](IMPLEMENTATION.md#vocabulary-annotations-are-declared-not-configured).

### Annotations beyond the reference model

The reference model's seven annotations exercise three terms. That is too little to say anything about how
far annotations are supported, so this server declares **69 annotations over 24 terms** of four
vocabularies - a deliberate deviation from the reference EDMX, and the one place where this repository adds
to the model rather than reproducing it.

| Vocabulary | Terms used | Where |
| --- | --- | --- |
| `Core` | `Description`, `Computed`, `ComputedDefaultValue`, `Immutable`, `Permissions`, `AcceptableMediaTypes`, `AdditionalProperties`, `OptimisticConcurrency`, `AlternateKeys` | types, properties, enum types and members, sets, the singleton, the container, functions, actions, parameters |
| `Measures` | `ISOCurrency`, `Unit`, `Scale`, `DurationGranularity` | the money, weight and duration properties |
| `Validation` | `Minimum`, `Maximum`, `Pattern`, `MaxItems` | `ISBN`, `AgeRating`, `PageCount`, `Condition`, `PopularityScore`, `Keywords`, `Search`'s `MaxResults` |
| `Capabilities` | `SearchRestrictions`, `SupportedFormats`, `BatchSupported`, `KeyAsSegmentSupported`, `QuerySegmentSupported`, `AsynchronousRequestsSupported`, `CrossJoinSupported` | `Container/Media` and the container |

Four of them are not written at all but **derived from the EF Core model**, which already states the same
fact: `Core.OptimisticConcurrency` from `IsConcurrencyToken()`, the `Precision`/`Scale` facets from
`HasPrecision`, `OnDelete` from `DeleteBehavior.Cascade`, and `Core.Description` on `Copy/Location_` from a
column comment that also becomes `COMMENT ON COLUMN` in `db/01-schema.sql`. What that buys is one source
instead of two; what it costs, and the six EF facts that look translatable and are not, is in
[IMPLEMENTATION.md](IMPLEMENTATION.md#what-ef-core-already-knows-is-not-stated-twice).

**Nothing is claimed that was not checked.** Every capability term on the container has a request behind it
in [`test/annotations.http`](test/annotations.http) - `$batch` and `POST …/$query` answer 200, so those are
`true`; `$crossjoin` answers 404 and `Prefer: respond-async` is ignored rather than honoured, so those are
`false`. Every constraint term is satisfied by the seeded data, shown by a request in the same file, and
every `Core.AcceptableMediaTypes` matches the `Content-Type` the stream actually comes back with.
`Capabilities.KeyAsSegmentSupported="true"` is worth singling out: `/Members/1` really does resolve to the
same entity as `/Members(1)`, which the reference model never asks about.

Constraint terms are **declarative only** - `Validation.Maximum` does not make the service refuse a larger
value, exactly as `Core.Computed` does not make it refuse a write. That is the same ⚠️ as the managed
property terms above, and the reason the seed is checked against them instead.

`Community.UrlEscapeFunction` is the one vocabulary left unused: it says a function may be called without
its name, which no operation in this model does, and a term stating a behaviour the server does not have
would be a false claim.

### What could be annotated

The mechanism covers **57 terms** of the OASIS vocabularies - every term of `Core`, `Measures`,
`Validation`, `Capabilities` and `Community` whose value is a primitive, an enum, a tag or a collection of
primitives, on any target: entity and complex types, properties, navigation properties, enum members,
entity sets, singletons, the container, operations and their parameters. Verified against `$metadata` for
one term of every shape and every target kind.

| Not covered                                                                                                                                     | Cause               |                                                                                                                       | 
|-------------------------------------------------------------------------------------------------------------------------------------------------|---------------------|-----------------------------------------------------------------------------------------------------------------------|
| record-valued terms (all of `Authorization`, 21 of `Capabilities`, `Core.Revisions`/`Links`/`Example`, `Validation.AllowedValues`/`Constraint`) | this implementation | C# attribute arguments cannot carry an object graph; still buildable by hand, as `Capabilities.SearchRestrictions` is |
| `Validation.Exclusive`, `Core.AppliesViaContainer`, `Core.RequiresType`, `Core.SchemaVersion`, `Core.DefaultNamespace`                          | the model           | they annotate a `Schema`, a `Term` or another annotation - constructs the model builder does not expose               |

## Deviations from the reference EDMX

### Behaviour

One, and it is chosen rather than forced: **only UTC `Edm.DateTimeOffset` values are accepted**, in a
request body and in a `$filter` literal alike. Anything with a non-zero offset is answered with 400 rather
than converted. The spec permits any offset, so this is a real deviation; it is made deliberately, because
UTC end to end is best practice and because refusing is honest where silently reinterpreting the client's
value would not be. See the summary above and `test/limitations.http`.

The timestamps themselves are stored as `timestamptz` at microsecond resolution, against the `Precision=7`
(100 ns) the reference model declares - three decimal places finer than Postgres keeps. Nothing in the seed
or in any payload observed reaches that resolution.

Two forced from below rather than chosen:

- the alternate key's `PropertyRef` carries an `Alias`, because `Microsoft.OData.Edm` throws a
  `NullReferenceException` at startup without one
- addressing by that key needs the type cast (`/Media/Library.Catalog.PrintMedium(ISBN='…')`), because
  `ISBN` is declared on `PrintMedium` while the entity set is of `Medium` - which is what the spec asks
  for, and the same shape containment already has here

And two chosen. **62 vocabulary annotations the reference model does not declare**, listed under
*Annotations beyond the reference model* above: three terms in the reference EDMX say too little about
whether annotations are supported at all, so the model is annotated properly instead - descriptions, units
and currencies, value constraints, and the capabilities the container actually has.

And **three `OnDelete="Cascade"` beyond the one the reference model declares**, on `Member/Reservations`,
`Medium/Copies` and `Audiobook/Chapters`. These are not additions to the model so much as a correction of
it: all four associations have cascaded in the database all along - `test/crud.http` has shown
`Medium`→`Copies` doing it since before this - and only one of the four said so. They are now taken from
EF's `DeleteBehavior` rather than written by hand, so the declaration cannot fall behind the behaviour
again.

Both add to the metadata and change no response; each is verified in `test/annotations.http`.

Beyond that, everything the reference model declares is emitted except `TypeDefinition`, `Unicode` and the
six redundant `SRID` facets.
