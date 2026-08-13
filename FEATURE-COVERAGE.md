# ASP.NET Core OData and the "Library" OData V4 test model

How far ASP.NET Core OData reproduces
[`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml), and
where it cannot.

Measured against **.NET 10.0.10** (the current LTS) with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0) and
**Microsoft.EntityFrameworkCore.Sqlite 10.0.11**. Every statement below was
verified against the emitted `$metadata` and against the running service - by diffing the metadata
mechanically, not by reading it. Claims about what the *libraries* can or cannot do were checked against
their actual API surface by reflection and against isolated probe models, not inferred from this
service's behaviour.

The reference model is a deliberately feature-dense probe of the OData spec, not a benchmark. A server
does not have to implement all of OData. So this document separates *what the library cannot express* from
*what this implementation simply does not do*.

Since the store became a real database (SQLite, in memory, through EF Core), there is a third thing to
separate out: *what the persistence layer costs*. The query options are no longer a LINQ-to-Objects
exercise - `[EnableQuery]` hands them to a provider that has to translate them to SQL - so a handful of
them now behave differently for reasons that have nothing to do with OData. Those are collected under
[What the persistence layer costs](#what-the-persistence-layer-costs), which also records the one place
where the change turned out to *fix* a conformance defect rather than cost anything.

## Summary

The **protocol and operation surface is reproduced completely**: all 20 entity types, 9 complex types,
2 enums, 88 properties, 12 navigation properties, 10 entity sets, the singleton and all 29 operations -
including both overload pairs, which is the part most implementations lose.

**Model metadata detail** comes through as well, on all but two counts. All six `Partner` attributes, both
vocabulary annotations that used to be missing and addressing by alternate key are in place - each
through an API that is easy to overlook, which is why they are written up below rather than just ticked
off. The six `SRID` attributes are absent but carry the value CSDL defines as the default, so nothing is
lost; ODL's own CSDL writer drops them from a hand-built model too. What the model builder genuinely
cannot express is two attributes: `TypeDefinition` and `Unicode`.

| Area                                    | Verdict                                                     |
| --------------------------------------- | ----------------------------------------------------------- |
| Entity types, inheritance, keys          | complete, three levels deep, composite key included          |
| Complex types incl. abstract base        | complete                                                     |
| Enums incl. flags and non-ASCII members  | complete                                                     |
| Operations (14 functions, 14 actions)    | complete, both overload pairs survive                        |
| Query options                            | complete, incl. `$query`; `$search` only with a hand-written binder |
| Containment, media entities, open types  | complete, streams and `$ref` served in every position         |
| Navigation properties incl. `Partner`    | complete, both sides related, `OnDelete` intact              |
| Model metadata detail                    | **2 attributes not expressible**, 6 redundant - see below     |
| Vocabulary annotations                   | 7 of 7, alternate key addressable via the type cast; the four managed-property terms are emitted but never enforced, and `Core.Permissions` comes out in the wrong shape |
| Query options against a real database     | all translate to SQL; `Edm.Date`/`Edm.TimeOfDay` need a replacement filter binder, and the date-part functions over `Edm.DateTimeOffset` are the one casualty - see below |
| Null in `$filter`                        | conformant, but only over a query provider - bound over a `List<T>` the library applies three-valued logic, which OData does not specify for `ne` or a negated comparison |

## What the model builder cannot express

These are limits of `Microsoft.OData.ModelBuilder` 2.0.0, not of this implementation. Each was checked
against the library's API surface, and against what ODL's CSDL writer does with a hand-built `IEdmModel`,
before being recorded here - "the builder has no method for it" is a claim about the library, so it was
verified against the library, not inferred from this service's metadata.

### `TypeDefinition`

`Library.Catalog.ISBN` - a named `Edm.String` with `MaxLength="13"` - has no equivalent. The builder has
no concept of a type definition; the property is emitted as plain `Edm.String`. The `MaxLength` facet is
kept, so nothing is lost semantically, but the named type is gone and with it the intent that ISBN is a
type rather than a string that happens to be short.

The layer below can do it: `EdmTypeDefinition` + `EdmTypeDefinitionReference` emit a `<TypeDefinition>`
element, and `AddRouteComponents` takes any `IEdmModel`. So the wall is the fluent builder, not the
stack - at the price of hand-building the model.

### `Unicode="false"`

`Copy/Location_` is declared non-unicode. There is no `IsUnicode` on any property configuration;
`MaxLength` and `Precision` are the only facets it exposes. Same shape as `TypeDefinition`: the Edm API
expresses it (`EdmStringTypeReference(..., isUnicode: false)` emits `Unicode="false"`), the builder
does not.

### `SRID` on spatial properties (6 occurrences) - and why it costs nothing

`Edm.GeographyPoint SRID="4326"` and `Edm.GeometryPoint SRID="0"` lose their SRID facet; there is no
`SRID` on any property configuration.

The diff makes this look like a loss, and it is not. CSDL defines the facet's default as 4326 for
`Geography` types and 0 for `Geometry` types - exactly the two values the reference model spells out. The
declarations are redundant, and a client reading only the metadata resolves the same effective SRID
either way.

It is not even a builder limitation. ODL's CSDL writer drops a default SRID from a hand-built model too:
set 3857 on a `GeographyPoint` and `SRID="3857"` is emitted, set 4326 and nothing is. A non-default SRID
would be the interesting case, and the reference model does not have one.

The values are right in the payload as well: `Branch.Location` serialises as GeoJSON including
`"crs": {"name": "EPSG:4326"}`.

## What works, including the parts that usually do not

### Both overload pairs

The reference model contains two deliberate overload pairs, and **both survive into the metadata and are
callable**:

- `Search(Term)` and `Search(Term, MaxResults)` - same name, differing parameter count. Both are
  *callable*, but only one endpoint is ever registered: OData resolves the function by name, so a second
  action with the longer route template is never selected and its extra parameter stays unbound. The
  request answers 200 with the unlimited result. One action serves both overloads and reads `MaxResults`
  off the URL.
- `AvailableCopies` bound once to a single `Medium` and once to a `Collection(Medium)`

### Binding parameter names

The builder names every binding parameter `bindingParameter`. That breaks `EntitySetPath="medium/Copies"`,
which refers to the parameter by name. `SetBindingParameter(name, type)` fixes it, so the model uses the
reference names (`medium`, `member`, `copy`, `loan`, `loans`, `media`).

### Inheritance, containment, media entities, open types

The three-level hierarchy (`Medium` → `PrintMedium` → `Magazine` → `TradeJournal`) is emitted with the
abstract flags intact. Contained entities work, and are addressable exactly as the spec requires - through
the type cast, since `Chapters` is declared on `Audiobook`:

```
GET /Media(<id>)/Library.Catalog.Audiobook/Chapters     200
GET /Media(<id>)/Chapters                               404
```

The open type carries its dynamic properties through, and `Edm.Untyped` is annotated with its runtime
type in the payload:

```json
"Appraisal": 12500,
"Insured": true,
"ExtraData@odata.type": "#String",
"ExtraData": "provenance unknown"
```

The flags enum round-trips including the non-ASCII member: `"Amenities": "WheelchairAccessible, Café"`.

### Query options in the request body: `POST <resource>/$query`

A URL carrying a long `$select` or `$filter` runs into the hosting environment's limit on the request
line - Kestrel's default is 8 KB, and it answers `414` well before the query itself becomes unreasonable.
OData 4.01 therefore allows the same query to travel in the body: `POST` to the resource with `/$query`
appended and the query string as `text/plain`.

`UseODataQueryRequest()` implements it, and **where it sits decides whether it works**: the middleware
rewrites the request into the equivalent GET, so it has to run *before* `UseRouting()` - behind it the
route for `/$query` does not exist and the request is refused with `405`. Nothing else changes; no
controller learns that the query arrived in a body.

The body is treated as a query string, which includes percent-decoding: `%24select=Title` is accepted
exactly like `$select=Title`. That matters for generated clients, which tend to encode the option names
along with the values.

```
POST /Media/$query          body: $filter=Language eq 'de'     200, filtered
POST /Media(<id>)/$query    body: %24select=Title              200, single entity, projected
GET  /Media?$filter=<8 KB of literals>                         414
POST /Media/$query          body: the same 8 KB filter         200
```

### `Core.OptimisticConcurrency`

Expressible via `[ConcurrencyCheck]`, emitted, and effective: `Copy` answers with `@odata.etag` in the
payload.

Since the store became a database the token also does something below the protocol: EF puts the original
`Condition` into the `WHERE` clause of every `UPDATE`, so two writers racing on the same copy collide for
real. The HTTP half is still missing, and it is missing the same way it was before - the controllers never
read `If-Match`, so a request carrying a stale ETag is accepted and no `412` is ever returned. The
annotation is honest about the model; the enforcement is one layer short.

### `Partner` on both sides of every association

All six `Partner` attributes are emitted - `Medium/Copies` ↔ `Copy/Medium`, `Member/Loans` ↔
`Loan/Member`, `Publisher/Books` ↔ `Book/Publisher`.

This one hides well enough to look impossible: `NavigationPropertyConfiguration.Partner` has an
`internal` setter, there is no `WithMany`/`WithRequired`/`HasPartner`, and the convention builder does
not infer a partner even though both sides are declared. But `HasRequired` and `HasOptional` each carry a
three-argument overload whose last argument *is* the partner - in a single-valued and a
collection-valued flavour - and one call sets `Partner` on both navigation properties:

```csharp
copy.HasRequired(c => c.Medium!, (c, m) => c.MediumId == m.Id, m => m.Copies);
loan.HasRequired(l => l.Member!, null, m => m.Loans);
builder.EntityType<Book>().HasOptional(b => b.Publisher!, null, p => p.Books);
```

The referential constraint in the middle may be `null`, which is what makes the last two work: unlike
`Copy/Medium`, those associations have no foreign key property in the reference model to constrain
against. `OnDelete="Cascade"` on `Member/Loans` survives next to it.

### `Core.AlternateKeys`, and addressing by one

`HasAlternateKeys` sits on `EntityTypeConfiguration<T>` as well as on `EntitySetConfiguration<T>`, and
the choice is not cosmetic: routing resolves the annotation **on the entity type**
(`GetDeclaredAlternateKeysForType`). Annotated on the entity set, `$metadata` looks right while the
address keeps answering 404. The reference model annotates the type, which is also the form that works.

Because `ISBN` is declared on `PrintMedium` and the entity set is of `Medium`, the address needs the type
cast - the same rule containment already follows here:

```
GET /Media/Library.Catalog.PrintMedium(ISBN='9783518188002')   200, full OData payload
GET /Media(ISBN='9783518188002')                               404
```

The route template has to be spelled out; a conventional `Get(string keyISBN)` action is not matched and
the request runs off the end of the middleware pipeline. Resolving an alternate key *outside* routing -
in `$filter`, in `$id` - additionally needs `AlternateKeysODataUriResolver` in the per-route container.

One deviation from the reference EDMX is forced, see the third trap below: the emitted `PropertyRef`
carries `Alias="ISBN"`, which the reference model omits.

### `Capabilities.SearchRestrictions`

`HasSearchRestrictions().IsSearchable(true)` on the entity set configuration, emitted on
`Container/Media` as in the reference model. `$search` itself works, see the trap about the per-route
container below.

### The managed-property terms — all four declared, none enforced

Both are expressible, in the same two-step shape the builder uses for every vocabulary term
(`HasComputed().IsComputed(true)`, `HasImmutable().IsImmutable(true)`), and both are emitted as external
targets with the fully qualified term name:

```xml
<Annotations Target="Library.Catalog.Medium/PopularityScore">
  <Annotation Term="Org.OData.Core.V1.Computed" Bool="true" />
</Annotations>
<Annotations Target="Library.Circulation.Loan/LoanedAt">
  <Annotation Term="Org.OData.Core.V1.Immutable" Bool="true" />
</Annotations>
```

Nothing then acts on either of them. `Delta<T>` records whatever the payload set and `delta.Patch()`
writes all of it through; neither the deserializer nor the routing layer consults the two terms.
Measured against the running service:

```
PATCH /Loans(8888…)  {"LoanedAt": "1999-01-01T00:00:00Z"}   204, and LoanedAt is 1999-01-01
PATCH /Media(1111…)  {"PopularityScore": 1.0}               204, and PopularityScore is 1.0
```

So the value is neither dropped nor rejected - it is applied, and the client is told nothing. This is
left as it is rather than corrected in the controllers: the terms describe an intent to a client that
reads `$metadata`, and what this document is for is recording that the runtime does not share it.

`LoansController` carries a `Patch` action for no other reason than to make this observable - the term
only says anything about an update, and the set was read-only before.

`Core.ComputedDefaultValue` on `Member/ActiveSince` and `Core.Permissions` (`Read`) on `Member/Balance`
complete the set, through the same two-step shape. Neither is enforced either, for the same reason.

**`Core.Permissions` comes out in the wrong shape, though.** The term is typed `Core.Permission` - an
enum - so its value belongs on the annotation itself, which is what CAP emits:

```xml
<Annotation Term="Core.Permissions" EnumMember="Core.Permission/Read"/>
```

The model builder wraps it in a record with a property that repeats the term's name:

```xml
<Annotation Term="Org.OData.Core.V1.Permissions">
  <Record><PropertyValue Property="Permissions">
    <EnumMember>Org.OData.Core.V1.Permission/Read</EnumMember>
  </PropertyValue></Record>
</Annotation>
```

Every `VocabularyTermConfiguration` in the library builds a record, which is right for the terms whose
type *is* a record and wrong for a term whose type is a primitive or an enum. The consequence is not
cosmetic: a client reading the vocabulary as written finds no value where one should be, and a
generated client therefore cannot act on the term at all - odata2ts ignores it, because the annotation
holds a `Record` rather than one of the constant forms it evaluates. The other three terms are tags,
whose `Bool` the builder does put in the right place, which is why only this one is affected.

## What the persistence layer costs

The store is SQLite, held in memory, through EF Core. That was a deliberate change: with the entity sets
exposed as `List<T>`, `[EnableQuery]` applied every query option in LINQ to Objects, where nothing can
fail to translate and nothing can be answered wrongly. Against a database the options have to become SQL,
which is what a consumer of a real OData service meets - and what it costs is worth writing down.

The short version: **every query option in the reference model's reach still works**, one family of
functions excepted; the things that had to be given up are all *representation*, not protocol; and one
thing got better, in that `$filter` over a null now follows the spec where it did not before.
Verified by running the same 61 reads and 50 mutations against the previous in-memory build and this one
and diffing every response: 57 of 61 reads and 49 of 50 mutations are byte-identical, and each difference
is accounted for below.

### What SQLite cannot store, and what it was traded for

Four types in the reference model have no faithful SQLite column. Each goes through a value converter in
[`Data/ValueConversions.cs`](src/LibraryService/Data/ValueConversions.cs):

| Type                                              | Stored as                              | What it costs                                                                                                                                                                                                |
|---------------------------------------------------|----------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Edm.Geography*` / `Edm.Geometry*` (6 properties) | WKT text                               | `geo.*` functions can never translate. They did not work over LINQ to Objects either, so nothing is lost in practice.                                                                                        |
| `Edm.Decimal` (`Balance`, `LateFee`)              | integer scaled by the declared `Scale` | nothing - see below                                                                                                                                                                                          |
| `Edm.DateTimeOffset` (5 properties)               | UTC ticks                              | the offset, and the date-part functions (`hour()`, `year()`, `now()`) - see below. Only the instant survives, so `+02:00` reads back as the same moment in UTC. Every timestamp in the model is UTC already. |
| `Edm.Duration`                                    | ticks                                  | the date-part functions, as above                                                                                                                                                                            |
| open type's dynamic properties, `Edm.Untyped`     | JSON                                   | they can never appear in `$filter` or `$orderby`                                                                                                                                                             |

`Microsoft.Spatial` is the interesting one: EF Core's spatial support is NetTopologySuite-only and would
additionally want the SpatiaLite native extension, so the type system OData itself speaks has no EF
provider at all. WKT round-trips it exactly, SRID included, and `$metadata` and every payload are
unchanged.

**Decimal deserves the detail**, because the obvious mapping is the dangerous one. SQLite has no decimal
type. Scaling to an integer keeps the value exact and both work, and it turns the
`Precision`/`Scale` facets the model already declares into the converter's contract. `DateTimeOffset` and
`Duration` went the same way for the same reason, except that EF refused to translate those outright
instead of answering wrongly - and there the trade is not free, since the extraction functions go with it.

### `Edm.Date` and `Edm.TimeOfDay` are not compared as values - which takes a replacement binder

The stock filter binder compares neither type directly. It takes both operands apart and rebuilds them as
a single number, then compares those:

```
$filter=PublicationDate gt 2000-01-01  ->  WHERE CAST(strftime('%Y', "PublicationDate") AS INTEGER) * 10000
                                               + CAST(strftime('%m', …) AS INTEGER) * 100
                                               + CAST(strftime('%d', …) AS INTEGER) > @p

$filter=OpensAt gt 09:30:00            ->  (long)"OpensAt".Hour * 36000000000 + …   no translation, 500
```

Over `List<T>` that is merely roundabout. Over a database the date form still answers correctly but can
never use an index - the column never appears on its own - and the time-of-day form has no SQL at all, so
the request failed outright. Not a storage problem: converting the column to ticks was tried first and
does not help, because the arithmetic is built from `.Hour`/`.Minute` before the provider ever sees it.

The behaviour is not configurable - `ExpressionBinderHelper.CreateDateBinaryExpression` and its time
counterpart are internal - so
[`Query/DateComparisonBinder.cs`](src/LibraryService/Query/DateComparisonBinder.cs) replaces the filter
binder and restates both as one comparison in the property's own CLR type. The approach comes from
[OData/AspNetCoreOData#1473](https://github.com/OData/AspNetCoreOData/issues/1473), where the same
arithmetic shows up as `DATEPART` against SQL Server:

```
WHERE "m"."PublicationDate" > '2000-01-01'
WHERE "b"."OpensAt" > '09:30:00'
```

Both are sargable now, and `$filter` on `Edm.TimeOfDay` works. Every comparison operator and both
boundaries were checked against the in-memory build, which is the reference for what the answers should
be. The binder stands down when null propagation is on - a LINQ-to-Objects source - where the base
implementation's three-valued `bool?` is what the surrounding expression expects.

### The date-part functions over `Edm.DateTimeOffset` and `Edm.Duration`

The one place where a storage decision is visible in the protocol surface:

```
GET /Loans?$filter=hour(LoanedAt) eq 10               500   (200 over the in-memory store)
GET /Loans?$filter=year(LoanedAt) eq 2026             500
GET /Loans?$filter=LoanedAt lt now()                  500
GET /Loans?$filter=date(LoanedAt) eq 2026-06-01       200
GET /Loans?$filter=LoanedAt gt 2020-01-01T00:00:00Z   200
GET /Loans?$orderby=LoanedAt                          200
```

`LoanedAt` is stored as an integer tick count, and no SQL pulls an hour back out of one. The alternative
is worse rather than better, and was measured rather than assumed: with EF's default mapping **every one**
of those requests fails, comparison and `$orderby` included, and the timestamps serialise differently as
well. Ticks buy the operators the reference model exercises and cost the extraction functions.
`Edm.Date` is unaffected - `year(PublicationDate)` works - because a date needs no converter.

Everything else translates: `$apply` with `groupby` and `aggregate`, `$compute`, nested `$expand` with its
own `$filter`/`$orderby`/`$top`, `$orderby` across a navigation property, `$filter` on a complex property,
`Keywords/$count`, `Keywords/any(...)`, enum `eq` and flags `has`, `isof`, and every string function the
model reaches.

### A failing query option must fail loudly - which took a middleware

Worth recording, because the default behaviour is the worst possible one. OData serialises the response
while it enumerates the `IQueryable`, straight onto the network. Over the in-memory store that enumeration
could not fail. Over a database it can, and by the time EF throws, the `200` and the opening bytes are
already sent:

```
HTTP/1.1 200 OK
{"@odata.context":"…#Branches(Name)","value":[          ← and nothing more
```

A truncated body under a success status, which makes "this server cannot answer that" indistinguishable
from "no rows matched" - the one failure mode a server that exists to be asserted against must not have.
`Program.cs` therefore buffers the response and turns an escaped exception into an honest `500`. It does
not make anything translate; it only ensures a limit is visible. The `500`s quoted above are it working.

### What the database gained

Not everything is a cost. Three things stopped being decoration:

- **Cascading delete happens.** `DELETE /Media(<id>)` now removes the medium's copies, because the
  reference model's cascade is declared on the relational side too. Previously the copies stayed behind,
  reachable at `/Copies` with a required `Medium` that no longer existed.
- **`Copy.Condition` is a real concurrency token.** EF puts the original value into the `WHERE` of every
  `UPDATE`, so two writers racing on one copy genuinely collide. Note what this does *not* add: the
  controllers still never check `If-Match`, so the HTTP precondition is unenforced and a stale ETag is
  still accepted - see [`Core.OptimisticConcurrency`](#coreoptimisticconcurrency).
- **`$batch` sub-requests each get their own unit of work**, since the `DbContext` is scoped. A `PATCH` in
  one sub-request is visible to a `GET` in the next, and each commits on its own.

### The two payload differences

Everything else is byte-identical to the in-memory build. These two are not:

|                                    | before   | now    | why                                                                                                                                                                                                       |
|------------------------------------|----------|--------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Member.Balance` for a whole value | `0`      | `0.00` | The scaled-integer converter reconstructs at the declared `Scale`, so a decimal always carries it. `12.50` was already `12.50`; it is `0` that was inconsistent, because the seed wrote the literal `0m`. |
| `GET` a copy of a deleted medium   | `200`    | `404`  | The cascade above.                                                                                                                                                                                        |

Collection order without `$orderby` is *not* among them, though it took work to keep: EF groups the
inserts of one `SaveChanges` by entity type, which permuted `/Media` into alphabetical order by CLR type.
Nothing in OData promises an order here, but consumers had a stable one, so
[`LibrarySeed`](src/LibraryService/Data/LibrarySeed.cs) inserts media and copies one at a time to preserve
it.

### A trap the change tracker sets

Every key in this model is caller-assigned - fixed GUIDs in the seed, `max(Id) + 1` for members - because
a test server whose keys depend on insert order is one nobody can assert against. That removes the signal
EF uses to tell a new entity from an existing one:

```csharp
var reservation = new Reservation { Id = Guid.NewGuid(), … };
member.Reservations.Add(reservation);   // reached only through a tracked entity
db.SaveChanges();                       // → UPDATE … WHERE Id = … → 0 rows → throws
```

Reaching a new entity solely through a tracked entity's navigation property is not enough. EF finds it
during change detection, sees a key it did not generate, concludes the row exists, and issues an `UPDATE`
that matches nothing. The entity has to be `Add`ed to its own set; linking it is a second step. This bit
both the seed and `Reserve`, and it is silent until `SaveChanges` runs.

## Three traps worth knowing

Both cost real time and neither produces an error - the service builds and runs, it just does the wrong
thing.

### `Namespace` drags every type with it

`ODataConventionModelBuilder.Namespace` names the entity container. It also becomes the namespace of every
type, so a model with four schemas silently collapses into one. The types have to be put back explicitly
from their CLR namespaces afterwards.

Enums are worse: one reached only through a property is registered so late that a namespace fix-up misses
it. They have to be registered explicitly with `EnumType<T>()` first.

### Query components resolve from a per-route container

`$search` without an `ISearchBinder` is **accepted and silently ignored** - 200 with the unfiltered set,
which a client cannot distinguish from a genuine result. Worse, registering the binder in the
application's service provider does not help: OData resolves query components from its own per-route
container. The registration compiles, runs, and is never used.

Only `AddRouteComponents(prefix, model, services => …)` works. With the binder in place `$search` filters
correctly, `AND`/`OR`/`NOT` included.

### An alternate key without `Alias` takes the whole service down

`Core.PropertyRef` has `Name` and an **optional** `Alias`; the reference model gives only the name, which
is legal CSDL. `Microsoft.OData.Edm` 8.4.0 does not survive it: `GetDeclaredAlternateKeysForType` throws
a `NullReferenceException` on such an annotation.

It does not surface as a failing request, because it happens while the attribute route template is being
parsed - during `MapControllers()`, at startup. The service does not start at all, and the stack trace
points at the URI parser rather than at the model:

```
System.NullReferenceException
   at Microsoft.OData.Edm.ExtensionMethods.GetDeclaredAlternateKeysForType(IEdmEntityType, IEdmModel)
   at Microsoft.OData.UriParser.AlternateKeysODataUriResolver.TryResolveAlternateKeys(…)
```

Setting the alias (`HasAlias("ISBN")`) avoids it. That is the one place where the emitted model carries
an attribute the reference EDMX does not - a library defect worked around, not a modelling decision.

## Verified behaviour

Everything below was executed against the running service.

| Request                                                                                    | Result                                                           |
|--------------------------------------------------------------------------------------------|------------------------------------------------------------------|
| `$metadata`, service document                                                              | 200                                                              |
| CRUD on entity sets (`POST`/`PATCH`/`DELETE`)                                              | 201 / 204 / 204                                                  |
| `PATCH /Media(<id>)` without `@odata.type`                                                 | 400 - the set's declared type is abstract, see below             |
| Composite key `Copies(MediumId=…,InventoryNumber=…)`                                       | 200 / 204 / 204 on GET / PATCH / DELETE                          |
| Second copy with an existing composite key                                                 | 409                                                              |
| Singleton `MainBranch`                                                                     | 200                                                              |
| `$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count`                     | 200, translated to SQL                                           |
| `$filter` on `Edm.Date` / `Edm.TimeOfDay`                                                  | 200, one direct comparison - needs the replacement filter binder |
| `$filter=hour(LoanedAt) eq 10` and the other date-part functions over `Edm.DateTimeOffset` | 500 - the tick storage has no hour to extract                    |
| `$apply`, `$compute`, nested `$expand` options, `$orderby` across a navigation property    | 200, translated to SQL                                           |
| `$search` (with binder)                                                                    | filters correctly                                                |
| `POST <resource>/$query` (query in the body)                                               | 200, options applied; a query too long for the URL succeeds      |
| `$apply=groupby((Language))`                                                               | 200, groups correctly                                            |
| `$compute`                                                                                 | 200                                                              |
| `$batch` (JSON)                                                                            | 200                                                              |
| Type-cast segment `/Media/Library.Catalog.Book`                                            | 200                                                              |
| Containment via type cast                                                                  | 200                                                              |
| All 14 functions, all 14 actions                                                           | 200 / 201 / 204 as declared                                      |
| `GET /Media/Library.Catalog.PrintMedium(ISBN='…')` (alternate key)                         | 200                                                              |
| `GET /Media(ISBN='…')` (alternate key without the type cast)                               | 404                                                              |
| `GET`/`PUT`/`DELETE` `/Media(<id>)/$value` (media entity stream)                           | 200 / 204 / 204                                                  |
| `GET`/`PUT`/`DELETE` `/Media(<id>)/Library.Catalog.Audiobook/Sample` (stream property)     | 200 / 204 / 204                                                  |
| `GET`/`PUT` `…/Chapters(<id>)/$value` (contained media entity)                             | 200 / 204                                                        |
| `$ref` on a collection-valued navigation property                                          | 200 / 204                                                        |
| `$ref` on a single-valued navigation property                                              | 200 / 204                                                        |
| Deep insert (`POST` with nested entities)                                                  | 201, children addressable in their own set                       |
| `DELETE` a medium that has copies                                                          | 204, copies cascade away with it                                 |
| Delta payload (`PATCH` on the collection)                                                  | 200, update + removal + upsert applied                           |
| `@odata.bind` / `{"@id"}` on create and update                                             | 201 / 204, link re-pointed, store intact                         |
| binding to `null`                                                                          | 204, link cleared                                                |

### Media entity streams

All three positions the reference model puts a stream in are served, read and write:

- `EBook` - a media entity *inside* the inheritance hierarchy
- `AudiobookChapter` - a media entity that is at the same time a *contained* entity, reached as
  `…/Library.Catalog.Audiobook/Chapters(<id>)/$value`
- `Audiobook.Sample` - a stream *property* rather than an entity's content

The content type given on `PUT` is stored and returned on the next `GET`. An entity that exists but has
no content yet answers `204`, not `404` - the distinction matters to a client deciding whether to upload.

`DELETE` clears the content and follows the same distinction: `404` means the *entity* is unknown, never
that it currently has no content, so deleting twice succeeds twice. Reporting `404` for an already empty
stream would contradict the `204` that `GET` answers for exactly that state.

### Creating a copy: the hand-written parser

`POST /Copies` reads the payload itself rather than through `[FromBody] Delta<Copy>`, because the OData
deserializer refuses a body binding a navigation property backed by a referential constraint. The price is
that every property has to be read explicitly - and three of them were missing, so `WeightKg`, `Status` and
`AcquisitionDate` were silently stored as their defaults while `PATCH` (which does go through `Delta<T>`)
kept them. They are read now.

A duplicate composite key is refused with `409`. Accepting it left two copies with the same key in the
store, after which *every* keyed read of that copy failed with "SingleResult must have zero or one
elements" - a store that could not be read from any more.

### `$ref`

Both cardinalities are served, and they differ in the verbs as the spec requires: a collection-valued
navigation property takes `POST` to add and `DELETE` with `$id` to remove, a single-valued one takes `PUT`
to set and plain `DELETE` to clear. A reference to a non-existent entity is refused with `400`.

### Patching an entity set whose declared type is abstract

`Media` is declared as `Library.Catalog.Medium`, which is abstract, so every entity in it is of a derived
type - and OData JSON requires the `@odata.type` annotation whenever an instance's type is derived from
the declared one. A partial update therefore has to name the type it is patching, even though the target
entity already exists and its type is not in doubt:

```
PATCH /Media(<id>)   {"Title": "Neu"}                                          400
PATCH /Media(<id>)   {"@odata.type": "#Library.Catalog.Book", "Title": "Neu"}  204
```

Worth knowing when generating a client: a `PATCH` builder that emits only the changed properties produces
the first shape, and against this entity set that is not a valid payload. The other entity sets are
declared with concrete types and take an untyped body.

The 400 is this implementation's, not the library's. Without the annotation the deserializer has no type
to construct and model binding hands the action a **null** delta, with no error of its own - so the shape
of the failure is a choice each implementation makes. Dereferencing it answers 500 to what is really a
malformed request; skipping it silently answers 204 to an update that was never applied, which is worse.
All five `PATCH` actions here take a nullable delta and answer 400, except where a null delta is still
usable: on `Copy` and `Member` the deserializer also rejects a body that binds a navigation to null, and
those two read the binding out of the raw body themselves, so they answer 400 only when there is no
binding either.

### Deep insert

`POST /Members` with nested `Loans` creates the parent and the children in one request.

Worth knowing, because it does **not** fail loudly: the deserializer fills the nested entities into the
parent's navigation property, but nothing registers them anywhere else. Left at that, the child is
reachable through `Members(3)/Loans` while carrying an all-zero key and being absent from `/Loans` - an
inconsistent state, not a partial one, and the request still answers 201. The controller therefore assigns
keys and registers nested entities in their own sets explicitly.

### Binding an existing entity: `@odata.bind` and `{"@id": …}`

Both notations work, on create and on update, and binding to `null` clears the link:

```json
"Location@odata.bind": "…/Branches(2)"      // OData 4.0
"Location": { "@id": "…/Branches(2)" }      // OData 4.01
"Location@odata.bind": null                 // clears the link
```

Getting there took two detours, and both are worth knowing because neither announces itself.

**Do not route a binding through `Delta<T>`.** The deserializer turns either notation into a *partial
instance* of the target type carrying only its key, and `Delta.Patch` treats that as a value to patch
**into the currently linked instance**. Binding a copy to another branch therefore does not re-point the
reference - it writes the new key into the branch that was linked before. Measured: after one such
request the store held

```
2 Central Library | 2 Suburban Branch
```

two entities with the same key, and the request answered `204`. Nothing looks wrong until the next read.
The bindings are therefore read from the raw request body and resolved against the store by key, which
needs `Request.EnableBuffering()` so the body survives model binding.

**A binding on a navigation property backed by a referential constraint is refused outright.**
`Medium@odata.bind` on a `Copy` - whose `MediumId` is tied to `Medium/Id` by a `ReferentialConstraint` -
makes the OData deserializer reject the *whole* body with `400`, before any controller code runs. There
is no way to accept it through `[FromBody]`; that action reads and parses the payload itself.

### Delta payloads (OData 4.01)

`PATCH /Members` with a `$delta` payload applies updates, removals and upserts in a single request:

```json
{ "@context": "…/$metadata#Members/$delta",
  "value": [ { "Id": 1, "Name": "…" },
             { "@removed": { "reason": "deleted" }, "Id": 2 },
             { "Id": 99, "Name": "…" } ] }
```

All three took effect, and the response is a proper delta payload carrying `@odata.removed` and
`@odata.id`. Entries arrive as `Delta<T>` or `DeltaDeletedResource<T>` in a `DeltaSet<T>`; an entry whose
key is unknown is treated as an upsert.

## Not implemented here

Every feature the reference model declares is served, and every attribute it declares is emitted except
the two the model builder cannot express (`TypeDefinition`, `Unicode`) and the six redundant `SRID`
facets - all of them above, with the reasoning.

Every vocabulary annotation the reference model declares is now emitted, `Core.Permissions` in a shape
of the model builder's own choosing - see above. None of the four managed-property terms changes what
the runtime accepts, which is the library's position rather than this server's.

Two deviations from the reference EDMX are deliberate and both are forced from below, not chosen:

- the alternate key's `PropertyRef` carries an `Alias`, because ODL throws without one
- addressing by that key needs the type cast (`/Media/Library.Catalog.PrintMedium(ISBN='…')`), because
  `ISBN` is declared on `PrintMedium` while the entity set is of `Medium` - which is what the spec asks
  for, and the same shape containment already has here
