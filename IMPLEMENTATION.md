# How this server is built

Everything the "Library" model costs in code: the places where a convention does not do, the workarounds a
library defect forces, and what the persistence layer demands. The *result* of all this - which OData
features are covered and which are not - is in [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md); this file is the
reasoning behind the rows that carry an **impl** tick.

Measured against **.NET 10.0.10** with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0) and
**Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3** against **PostgreSQL 18**.

## The EDM model

### `Namespace` drags every type with it

`ODataConventionModelBuilder.Namespace` names the entity container. It also becomes the namespace of every
type, so a model with four schemas silently collapses into one. The types have to be put back explicitly
from their CLR namespaces afterwards (`AlignNamespacesWithClrTypes`).

Enums have to be registered explicitly with `EnumType<T>()` first.

### `Partner` on both sides of every association

`NavigationPropertyConfiguration.Partner` has an
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

### Binding parameter names

In order to persuade the builder to not name every binding parameter `bindingParameter`, 
we use `SetBindingParameter(name, type)`, so the model uses the
reference names (`medium`, `member`, `copy`, `loan`, `loans`, `media`).

### Overload pairs

`Search(Term)` / `Search(Term, MaxResults)` are both callable, but only one endpoint is ever registered:
OData resolves the function by name, so a second action with the longer route template is never selected
and its extra parameter stays unbound. One action serves both overloads and reads `MaxResults` off the URL.
`AvailableCopies`, bound once to a single `Medium` and once to a `Collection(Medium)`, needs nothing
special.

### Vocabulary annotations are declared, not configured

Annotations are written where the thing they describe is declared, and translated centrally:

```csharp
[Core.Computed]                     public double? PopularityScore { get; set; }
[Core.Permissions(Core.Permission.Read)] public decimal Balance { get; set; }
[Measures.Unit("kg")]               public float WeightKg { get; set; }
```

`src/LibraryService/Annotations/` holds one static class per OASIS vocabulary - `Core`, `Measures`,
`Validation`, `Capabilities`, `Community` - with one attribute per term, plus a generic
`[Annotation("Org.OData.Core.V1.LongDescription", "…")]` for everything without one. Covered are the terms
whose value is a **primitive, an enum, a tag or a collection of primitives**; a record-valued term
(`Capabilities.InsertRestrictions`, `Core.Revisions`, …) has no attribute, because C# attribute arguments
cannot carry an object graph.

**The attributes know nothing about CSDL.** Each carries a term name, a raw CLR value and an optional
qualifier; `AnnotationEmitter` resolves the term against the vocabulary that
`Microsoft.OData.Edm` itself ships as an `IEdmModel` (`CoreVocabularyModel.Instance` and its siblings),
reads its **declared type**, and builds the expression that type demands - `Bool` for a tag, `EnumMember`
for an enum, `PropertyPath` for a path, `Collection` of those for a collection. That single type-directed
step is the whole point; see the `Core.Permissions` trap below.

Three things are refused, and refusing means the service does not start - a silently dropped or wrongly
shaped annotation would be a wrong `$metadata`, which is what this repo exists to report on:

| Refused | Message |
| --- | --- |
| unknown term | `Unknown vocabulary term 'Org.OData.Core.V1.NoSuchTerm' on 'Library.Catalog.PrintMedium/ISBN'` |
| wrong target | `… cannot be applied to 'Library.Catalog.PrintMedium/ISBN': the vocabulary declares AppliesTo="Collection", the target is Property` |
| same term twice | `… is applied to … more than once with the same qualifier` |

The target check reads `AppliesTo` off the term itself, so the rule is the vocabulary's, never a list
maintained here.

### The model builder wraps every term value in a record

`VocabularyTermConfiguration` - what `HasPermissions()`, `HasComputed()`, `HasImmutable()` and the rest
build - emits a `Record` whatever the term's type is. For the tags that is invisible, because a record with
one boolean property happens to be what those terms declare. For `Core.Permissions` it is wrong: the term
is typed `Core.Permission`, an enum, so the value belongs on the annotation itself. The builder produced

```xml
<Annotation Term="Org.OData.Core.V1.Permissions">
  <Record><PropertyValue Property="Permissions" .../></Record>
</Annotation>
```

which is not valid CSDL: the value expression of an annotation has to match the term's declared type, and
a `Record` expression is only allowed where that type is a structured type. Emitting the annotation
directly gives the shape the term declares:

```xml
<Annotation Term="Org.OData.Core.V1.Permissions">
  <EnumMember>Org.OData.Core.V1.Permission/Read</EnumMember>
</Annotation>
```

The spec states an `EnumMember` constant either as an attribute on `edm:Annotation` (`EnumMember="…"`) or
as an `edm:EnumMember` child element. They are the same expression and a client has to read both;
`Microsoft.OData.Edm`'s XML writer chooses the element form, and that is what comes out here.

`Core.AlternateKeys` and `Capabilities.SearchRestrictions` are still built by the model builder, not by
this mechanism: both are record-valued, and `HasAlternateKeys` additionally feeds routing (next section).
Whether either should move is **open** - `AlternateKeys` only if the emitted annotation still satisfies
`GetDeclaredAlternateKeysForType`, which is the whole reason the alias is set. Which record-valued terms
are worth an attribute at all is open too, and is a question per term rather than one decision.

### Entity sets have no CLR declaration - and the generic configuration hides itself

An entity set, a singleton, an operation and its parameters are built by name, so there is nothing to put
an attribute on. They are annotated fluently instead, with the same attribute objects:

```csharp
builder.AnnotatableEntitySet<Copy>("Copies").Annotate(new Capabilities.TopSupported(false));
builder.AnnotateContainer(new Capabilities.ConformanceLevel(Capabilities.ConformanceLevelType.Advanced));
search.Parameter<int>("MaxResults").Annotate(new Validation.Maximum(100));
```

`AnnotatableEntitySet` exists because of a library trap. `builder.EntitySet<T>(name)` returns the *generic*
`EntitySetConfiguration<T>`, a wrapper whose `Configuration` property - the real, non-generic configuration
the builder keeps in `builder.EntitySets` - is **internal**. An annotation parked against the wrapper could
never be matched back to the entity set afterwards. `AddEntitySet(name, AddEntityType(typeof(T)))` is the
same declaration one level down, is public, is idempotent, and returns the configuration itself.

Operations are matched back to the built model by fully qualified name **and parameter names**: the
reference model has two overload pairs (`Search`, `AvailableCopies`), for which the name alone is
ambiguous.

### What EF Core already knows is not stated twice

EF Core and OData describe overlapping things. Where both have a word for the same fact, the persistence
model is the source and the EDM follows - `EfCoreTranslation` reads the EF model and configures the OData
builder from it. Four facts are carried over:

| EF Core | OData | Where it shows |
| --- | --- | --- |
| `.IsConcurrencyToken()` | `Core.OptimisticConcurrency` + `@odata.etag` | `Copy/Condition` |
| `HasPrecision(p, s)` | `Precision` / `Scale` facets | `Member/Balance` (9,2), `Loan/LateFee` (5,2) |
| `DeleteBehavior.Cascade` | `<OnDelete Action="Cascade"/>` | `Member/Loans`, `Member/Reservations`, `Medium/Copies`, `Audiobook/Chapters` |
| `HasComment(…)` | `Core.Description` | `Copy/Location_` |

**Some of it was already in agreement, by accident rather than design.** `[MaxLength]`, `[Required]`,
`[Key]`, `[NotMapped]`, `[Column]`, `[DefaultValue]` and `[ConcurrencyCheck]` are
`System.ComponentModel.DataAnnotations` attributes that *both* stacks read - the OData side through
`MaxLengthAttributeEdmPropertyConvention`, `ConcurrencyCheckAttributeEdmPropertyConvention` and friends. The
agreement ends the moment a model configures the same thing fluently, which is how most EF models are
written. `Copy/Condition` is deliberately declared with EF's fluent `IsConcurrencyToken()` and *not* with
`[ConcurrencyCheck]`, so that the annotation and the ETag exist only because they were translated.

**Three traps, in order of how much they cost.**

*The model has to be read at the right moment.* `ODataConventionModelBuilder` discovers properties and
navigations while building, so a translation that runs before `GetEdmModel()` sees a configuration with
almost nothing in it - only what was configured by hand - and quietly does nothing. Running it after is
too late: `Precision` and `IsConcurrencyToken` are builder concepts and no longer settable. The hook is
`builder.OnModelCreating`, which fires after the conventions and before the configuration is locked down.
The first version of this ran too early and produced a model with the precision facets, the concurrency
annotation and the comment all silently missing.

*Relational metadata is not in the runtime model.* `DbContext.Model` is read-optimized and throws
`"the requested configuration is not stored in the read-optimized model"` for a comment or a precision. It
takes `GetService<IDesignTimeModel>().Model` - see `DatabaseInit.MappingModel`, which builds it from a
context with a dummy connection string and never opens a connection, exactly as the schema generator does.

*Delete behaviour belongs to the foreign key, `OnDelete` to one navigation.* Both navigations of a foreign
key report its `DeleteBehavior`, while `<OnDelete>` on a navigation property means "deleting the entity
that declares this navigation deletes what it points at". Applied to the dependent's reference back to its
principal, it says that deleting a `Copy` deletes the `Medium`. Only the principal side may carry it -
`INavigation.IsOnDependent` is the test, and without it the model gained two cascades that claimed the
opposite of what the database does.

**What is deliberately *not* translated matters as much.** Each of these looks like a pendant and would
put a false statement into `$metadata`:

| EF Core | Why not |
| --- | --- |
| `ValueGenerated.OnAdd` | EF's convention for client-generated `Guid` keys - it is on `Medium/Id`, `Loan/Id`, `IdDocument/Id` and `Reservation/Id`, every one of which is assigned by the seed or a controller. `Core.ComputedDefaultValue` would be untrue |
| `ValueGenerated.OnUpdateSometimes` | a table-per-hierarchy artefact for a property not mapped on every subtype (`DVD/Duration`). Nothing to do with `Core.Computed` |
| `GetDefaultValue()` | the design-time model reports the *CLR type default* for every non-nullable property - including `false` for `Copy/IsLoanable`, whose declared default is `true` |
| unique index | the only one is the 1:1 foreign key `Member/IdDocumentId`; reading unique indexes as `Core.AlternateKeys` would invent an alternate key |
| `DeleteBehavior.SetNull` | used five times here, and CSDL has the action - but `NavigationPropertyConfiguration` exposes only `CascadeOnDelete()` |
| `IsUnicode(false)` | no vocabulary term, no builder API, and nothing to read it from either - see the `Unicode` row in FEATURE-COVERAGE.md |
| owned types, `ToJson()`, `HasDiscriminator`, `PrimitiveCollection` | storage shape; the EDM expresses the same structure independently |

### `Core.AlternateKeys` - three things have to line up

`HasAlternateKeys` sits on `EntityTypeConfiguration<T>` as well as on `EntitySetConfiguration<T>`, and
the choice is not cosmetic: routing resolves the annotation **on the entity type**
(`GetDeclaredAlternateKeysForType`). Annotated on the entity set, `$metadata` looks right while the
address keeps answering 404. The reference model annotates the type, which is also the form that works.

The route template has to be spelled out; a conventional `Get(string keyISBN)` action is not matched and
the request runs off the end of the middleware pipeline. Resolving an alternate key *outside* routing -
in `$filter`, in `$id` - additionally needs `AlternateKeysODataUriResolver` in the per-route container.

**An alternate key without `Alias` takes the whole service down.** `Core.PropertyRef` has `Name` and an
**optional** `Alias`; the reference model gives only the name, which is legal CSDL.
`Microsoft.OData.Edm` 8.4.0 does not survive it: `GetDeclaredAlternateKeysForType` throws a
`NullReferenceException` on such an annotation. It does not surface as a failing request, because it
happens while the attribute route template is being parsed - during `MapControllers()`, at startup. The
service does not start at all, and the stack trace points at the URI parser rather than at the model:

```
System.NullReferenceException
   at Microsoft.OData.Edm.ExtensionMethods.GetDeclaredAlternateKeysForType(IEdmEntityType, IEdmModel)
   at Microsoft.OData.UriParser.AlternateKeysODataUriResolver.TryResolveAlternateKeys(…)
```

Setting the alias (`HasAlias("ISBN")`) avoids it. That is the one place where the emitted model carries an
attribute the reference EDMX does not - a library defect worked around, not a modelling decision.

## Routing and the pipeline

### Query components resolve from a per-route container

`$search` without an `ISearchBinder` is **accepted and silently ignored** - 200 with the unfiltered set,
which a client cannot distinguish from a genuine result. Worse, registering the binder in the
application's service provider does not help: OData resolves query components from its own per-route
container. The registration compiles, runs, and is never used.

Only `AddRouteComponents(prefix, model, services => …)` works. That is where
[`MediumSearchBinder`](src/LibraryService/Query/MediumSearchBinder.cs), the replacement `IFilterBinder`
and `AlternateKeysODataUriResolver` are registered.

### `POST <resource>/$query` - middleware order decides

`UseODataQueryRequest()` rewrites the request into the equivalent GET, so it has to run *before*
`UseRouting()` - behind it the route for `/$query` does not exist and the request is refused with `405`.
Nothing else changes; no controller learns that the query arrived in a body. The body is treated as a
query string, which includes percent-decoding: `%24select=Title` is accepted exactly like `$select=Title`.

### A failing query option must fail loudly

Worth recording, because the default behaviour is the worst possible one. OData serialises the response
while it enumerates the `IQueryable`, straight onto the network. Over an in-memory store that enumeration
could not fail. Over a database it can, and by the time EF throws, the `200` and the opening bytes are
already sent:

```
HTTP/1.1 200 OK
{"@odata.context":"…#Branches(Name)","value":[          ← and nothing more
```

A truncated body under a success status, which makes "this server cannot answer that" indistinguishable
from "no rows matched" - the one failure mode a server that exists to be asserted against must not have.
`Program.cs` therefore buffers the response and turns an escaped exception into an honest `500`. It does
not make anything translate; it only ensures a limit is visible.

## Persistence

The store is **PostgreSQL 18** through EF Core (Npgsql). Against a database the query options have to
become SQL, which is what a consumer of a real OData service meets.

It runs *inside* the published image, next to the service, rather than in a second container - see
[The container](#the-container) below - so consuming this server is still `docker run -p 4004:4004` with
nothing to compose or mount. The data directory is rebuilt on every start, so a restart is a reset.

### What a relational column cannot store

Two things in the reference model, down from five. Each goes through a value converter in
[`Data/ValueConversions.cs`](src/LibraryService/Data/ValueConversions.cs):

| Type                                              | Stored as | What it costs                                                                                                                        |
|---------------------------------------------------|-----------|--------------------------------------------------------------------------------------------------------------------------------------|
| `Edm.Geography*` / `Edm.Geometry*` (6 properties) | WKT text  | `geo.*` functions can never translate. They did not work over LINQ to Objects either, so nothing is lost in practice.                 |
| open type's dynamic properties, `Edm.Untyped`     | `json`    | they can never appear in `$filter` or `$orderby` - though the URI parser refuses an undeclared property with 400 well before the store |

`Microsoft.Spatial` is the interesting one: EF Core's spatial support is NetTopologySuite-only and would
additionally want PostGIS, so the type system OData itself speaks has no EF provider at all. WKT
round-trips it exactly, SRID included, and `$metadata` and every payload are unchanged.

The JSON column is deliberately `json` and **not** `jsonb`. jsonb normalises the document, and key order is
not decoration here: the dictionary materialises from the stored JSON in that order, and OData writes the
dynamic properties out in the order it finds them - so jsonb silently reordered the open type's payload to
`Insured, Appraisal`. `json` keeps the text verbatim. What jsonb would have bought - indexing, containment
operators - is worth nothing when nothing can query the column anyway.

### What the move from SQLite removed

Until 2026-08-15 the store was SQLite in memory, and three further types needed converters, because SQLite
has no column for any of them: `Edm.Decimal` was scaled to an integer, `Edm.DateTimeOffset` and
`Edm.Duration` were stored as tick counts. That was not a free trade - it cost the date-part functions
(`hour()`, `year()`, `now()` all answered 500) and, in the case of `date()`, produced the only silently
wrong answer in the service.

Postgres has `numeric`, `timestamptz` and `interval`, so all three properties are now stored as themselves:

- **`Edm.Decimal`** is `numeric(9,2)` / `numeric(5,2)`, taken from the `Precision`/`Scale` the model
  already declares, so `$metadata` and the column now state the same thing and the database enforces it.
  `Member.Balance` still serialises `0.00` rather than `0` - that is the declared scale, not an artefact.
- **`Edm.DateTimeOffset`** is `timestamptz`, and every date-part function over it now translates.
- **`Edm.Duration`** is `interval`.

One converter did come back, and it is a guard rather than a conversion: Npgsql refuses a `DateTimeOffset`
whose offset is not zero, so a client sending `+02:00` - legal `Edm.DateTimeOffset` - would have got a 500
out of the provider. `ValueConversions.UtcOnly` turns that into a deliberate **400** naming the property
and the UTC value to send instead. It is applied to every `DateTimeOffset` in the model at once, in
`OnModelCreating`, because the UTC contract belongs to the service and not to any one entity, and it is
reached from both directions a timestamp can arrive from - a write body and a `$filter` literal - because
EF puts converters in front of both. `UtcOnlyException` exists purely so the error middleware can tell that
apart from a genuine server fault and answer 400 rather than 500. The deviation is recorded in
FEATURE-COVERAGE.md.

`Keywords` is now a native `text[]` rather than a JSON array, so `Keywords/any(...)` translates to an array
operation.

### Schema and seed

Neither is code. [`db/01-schema.sql`](db/01-schema.sql) and [`db/02-seed.sql`](db/02-seed.sql) are applied
by Postgres itself, from `/docker-entrypoint-initdb.d`, before the service is started - so the service has
no seeding path at all, no "has it been seeded yet" check, and no startup order to get wrong. The database
is already correct when the service first reaches it.

The schema is **generated, not hand-written**, because EF owns the mapping - the discriminator, the shadow
foreign keys, the column names owned types get - and two hand-maintained descriptions of one mapping would
drift. Regenerate it after changing the model:

```bash
dotnet run --project src/LibraryService -- --emit-schema ../../db/01-schema.sql
```

The seed is hand-maintained SQL. Statement order is load-bearing twice: across tables it satisfies the
foreign keys, and within a table it fixes the physical row order, which is what an unordered `/Media`
answers in. That order is not promised by OData, but consumers had a stable one and it is cheap to keep.
It also *replaced* a workaround - the old code seed had to call `SaveChanges` once per row, because EF
groups the inserts of one `SaveChanges` by entity type and permuted `/Media` into alphabetical order by CLR
type. Plain `INSERT`s have no such behaviour.

Every key in this model is caller-assigned - fixed GUIDs in the seed, `max(Id) + 1` for members - because a
test server whose keys depend on insert order is one nobody can assert against.

### The container

The published image carries a Postgres of its own. That is not the usual arrangement - two containers and
a compose file would be - but the single `docker run -p 4004:4004` is the contract consumers already
depend on, and a database is not a reason to make every consumer learn a new one.

[`docker-entrypoint.sh`](docker-entrypoint.sh) is the whole of it, and the order is the point: `initdb`,
start Postgres on the loopback interface, apply `db/*.sql` with `ON_ERROR_STOP=1`, only then `exec` the
service with a connection string in the environment. `set -e` means a broken seed exits the container
instead of serving a half-filled model. `exec` makes the service PID 1, so `docker stop` reaches it.

Two details that are easy to get wrong:

- **`su` resets `PATH`.** The `ENV PATH` in the Dockerfile applies to root; the postgres shell would not
  find `initdb` or `pg_ctl`, so the entrypoint calls them by absolute path.
- **Postgres 18 comes from PGDG**, not from Ubuntu 24.04, which ships 16. It has to be the same major
  version as `DatabaseInit.PostgresImage`, or a local run and the image would not be the same server.

Locally there is no entrypoint, so the service does it itself: with **no connection string configured** it
starts a Postgres container through Testcontainers, mapping the same `db/*.sql` into
`/docker-entrypoint-initdb.d`, and waits for it. That keeps `dotnet run --project src/LibraryService` a
one-liner with nothing installed but Docker, and it is why Testcontainers is a normal dependency of the
service project rather than a test-only one. A configured connection string always wins, which is how the
image bypasses this path entirely.

### A trap the change tracker sets

Caller-assigned keys remove the signal EF uses to tell a new entity from an existing one:

```csharp
var reservation = new Reservation { Id = Guid.NewGuid(), … };
member.Reservations.Add(reservation);   // reached only through a tracked entity
db.SaveChanges();                       // → UPDATE … WHERE Id = … → 0 rows → throws
```

Reaching a new entity solely through a tracked entity's navigation property is not enough. EF finds it
during change detection, sees a key it did not generate, concludes the row exists, and issues an `UPDATE`
that matches nothing. The entity has to be `Add`ed to its own set; linking it is a second step. This bit
both the seed and `Reserve`, and it is silent until `SaveChanges` runs.

### A read must not fill the change tracker

Every queryable handed to `[EnableQuery]` goes out `AsNoTracking`. Not only because tracking a read is
waste: `$select` on a complex property makes OData project the owned type by itself,

```
GET /Branches(1)?$select=Name,Address($select=*)
```

and EF refuses to track an owned entity apart from its owner - *"A tracking query is attempting to project
an owned entity without a corresponding owner in its result"*. The request failed outright, as a 400 for a
single entity and as a 500 for a collection, where the exception only arrives once the payload is already
being enumerated. `Address($select=City,Country)` was served all along: a projection of *some* of the owned
properties is a projection of values, and only the whole owned instance is an entity EF would have to
track.

The write paths query separately and stay tracked - `PATCH` and `DELETE` load their entity through their
own `FirstOrDefault`, and `RenewAll` mutates what it read. Untracking those would be silent: `SaveChanges`
writes nothing and still answers 204.

### What the database gained

Three things stopped being decoration:

- **Cascading delete happens.** `DELETE /Media(<id>)` removes the medium's copies, because the reference
  model's cascade is declared on the relational side too.
- **`Copy.Condition` is a real concurrency token.** EF puts the original value into the `WHERE` of every
  `UPDATE`, so two writers racing on one copy genuinely collide. Note what this does *not* add: the
  controllers never check `If-Match`, so the HTTP precondition stays unenforced and a stale ETag is
  accepted.
- **`$batch` sub-requests each get their own unit of work**, since the `DbContext` is scoped. A `PATCH` in
  one sub-request is visible to a `GET` in the next, and each commits on its own.

## Query

### `Edm.Date` and `Edm.TimeOfDay` are not compared as values

The stock filter binder compares neither type directly. It takes both operands apart and rebuilds them as a
single number, then compares those:

```
$filter=PublicationDate gt 2000-01-01  ->  WHERE EXTRACT(YEAR FROM "PublicationDate") * 10000
                                               + EXTRACT(MONTH FROM …) * 100
                                               + EXTRACT(DAY FROM …) > @p

$filter=OpensAt gt 09:30:00            ->  (long)"OpensAt".Hour * 36000000000 + …   no translation, 500
```

Over `List<T>` that is merely roundabout. Over a database the date form still answers correctly but can
never use an index - the column never appears on its own - and the time-of-day form has no SQL at all, so
the request fails outright. Not a storage problem, and verified twice: it failed the same way against
SQLite and against Postgres, because the arithmetic is built from `.Hour`/`.Minute` before the provider
ever sees it. The date form is the only one that changed with the move - SQLite needed `strftime` where
Postgres uses `EXTRACT`, and both are correct.

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
boundaries were checked against the in-memory build, which is the reference for what the answers should be.
The binder stands down when null propagation is on - a LINQ-to-Objects source - where the base
implementation's three-valued `bool?` is what the surrounding expression expects.

**It also has to stand down when the operand is not really a date.** This is the trap, and it produced a
wrong answer rather than an error for some time:

```
$filter=date(LoanedAt) eq 2026-06-01   ->   no match, for a loan that is plainly on that date
```

The library's `date()` does not truncate - it hands the whole timestamp through, which
`$compute=date(LoanedAt) as D` makes visible by returning `2026-06-01T10:00:00Z`. The other operand is
therefore a `DateTimeOffset`, not a `DateOnly`, and the binder used to convert the `Edm.Date` literal to
match it - producing midnight, and comparing 10:00 against 00:00.

So `Constant` now accepts **only** `DateOnly` for `Edm.Date` and `TimeOnly` for `Edm.TimeOfDay`, and
returns null for anything else, which hands the comparison back to the base implementation - whose
part-by-part arithmetic is roundabout but correct, and which Postgres translates. The binder takes over
only what it can state more directly.

This was found while moving to Postgres, but it was never a storage problem: the wrong answer had been
attributed to the tick storage, and survived its removal unchanged.

## Controllers

### Creating a copy: the hand-written parser

`POST /Copies` reads the payload itself rather than through `[FromBody] Delta<Copy>`, because the OData
deserializer refuses a body binding a navigation property backed by a referential constraint. The price is
that every property has to be read explicitly - and three of them were missing, so `WeightKg`, `Status` and
`AcquisitionDate` were silently stored as their defaults while `PATCH` (which does go through `Delta<T>`)
kept them. They are read now.

A duplicate composite key is refused with `409`. Accepting it left two copies with the same key in the
store, after which *every* keyed read of that copy failed with "SingleResult must have zero or one
elements" - a store that could not be read from any more.

### Binding an existing entity: `@odata.bind` and `{"@id": …}`

Both notations work, on create and on update, and binding to `null` clears the link. Getting there took two
detours, and both are worth knowing because neither announces itself.

**Do not route a binding through `Delta<T>`.** The deserializer turns either notation into a *partial
instance* of the target type carrying only its key, and `Delta.Patch` treats that as a value to patch
**into the currently linked instance**. Binding a copy to another branch therefore does not re-point the
reference - it writes the new key into the branch that was linked before. Measured: after one such request
the store held

```
2 Central Library | 2 Suburban Branch
```

two entities with the same key, and the request answered `204`. Nothing looks wrong until the next read.
The bindings are therefore read from the raw request body and resolved against the store by key, which
needs `Request.EnableBuffering()` so the body survives model binding.

**On a create, the bound stub has to be replaced before the graph is tracked.** The stub the deserializer
builds sits in the navigation property of an entity that is about to be `Add`ed, and `Add` tracks
everything it can reach as `Added` - so the request tries to `INSERT` the very row it was asked to link and
dies on its primary key:

```
23505: duplicate key value violates unique constraint "PK_Branches"
```

Every bound navigation was affected, at any depth: a book's publisher, a collector's item's storage
location, a nested copy's branch, a nested loan's copy. Over the in-memory store nothing of the sort
happened - there was no insert, only a reference being assigned - which is why the move to a database
turned a working feature into a 500 without a line of the create paths changing.

Which of the two arrived is a question about the *payload*: a bound stub and a deep-inserted entity that
brings its own key are the same object by the time the controller sees them. `NavigationBinding.Resolve`
therefore walks the JSON body alongside the object graph and swaps each bound navigation for the stored
entity, which is then tracked `Unchanged` and only linked. Keys are read through EF's own metadata, so a
composite one - `Copies(MediumId=…,InventoryNumber=…)` - needs nothing extra; a binding naming an entity
that does not exist answers 400 instead of failing on a foreign key.

**A binding on a navigation property backed by a referential constraint is refused outright.**
`Medium@odata.bind` on a `Copy` - whose `MediumId` is tied to `Medium/Id` by a `ReferentialConstraint` -
makes the OData deserializer reject the *whole* body with `400`, before any controller code runs. There is
no way to accept it through `[FromBody]`; that action reads and parses the payload itself.

### Deep insert

`POST /Members` with nested `Loans` creates the parent and the children in one request. Worth knowing,
because it does **not** fail loudly: the deserializer fills the nested entities into the parent's
navigation property, but nothing registers them anywhere else. Left at that, the child is reachable through
`Members(3)/Loans` while carrying an all-zero key and being absent from `/Loans` - an inconsistent state,
not a partial one, and the request still answers 201. The controller therefore assigns keys and registers
nested entities in their own sets explicitly.

### Media entity streams

All three positions the reference model puts a stream in are served, read and write, from
`Controllers/StreamControllers.cs`: `EBook` (a media entity inside the inheritance hierarchy),
`AudiobookChapter` (a media entity that is at the same time a *contained* entity) and `Audiobook.Sample`
(a stream *property* rather than an entity's content).

The content type given on `PUT` is stored and returned on the next `GET`. An entity that exists but has no
content yet answers `204`, not `404` - the distinction matters to a client deciding whether to upload.
`DELETE` clears the content and follows the same distinction: `404` means the *entity* is unknown, never
that it currently has no content, so deleting twice succeeds twice. Reporting `404` for an already empty
stream would contradict the `204` that `GET` answers for exactly that state.

### `$ref`

Both cardinalities are served from `Controllers/RefControllers.cs`, and they differ in the verbs as the
spec requires: a collection-valued navigation property takes `POST` to add and `DELETE` with `$id` to
remove, a single-valued one takes `PUT` to set and plain `DELETE` to clear. A reference to a non-existent
entity is refused with `400`.

### `LoansController.Patch`

Carried for no other reason than to make the `Core.Immutable` term observable - the term only says
anything about an update, and the set was read-only before.

### Action parameters: `{}` binds to null, not to an empty dictionary

A request body that carries **none** of the declared parameters hands the action a null
`ODataActionParameters`. An action that reads its parameters straight off that argument therefore
dereferences null and answers `500` before reaching its own "parameter is required" check - the request
that is *most* obviously malformed is the one that fails worst. Every action taking parameters declares
them nullable (`ODataActionParameters?`) and reads them through `ActionParameters.Get`, which folds the
null case into "not supplied".

What "not supplied" then means is decided by `$metadata`, not by convenience: a parameter with
`Nullable="false"` may not be omitted, so `ClosureDay/Date`, `YearEndClosing/Year`,
`Reserve/MemberId`, `CheckOut/MemberId` and `AssessCondition/NewCondition` answer `400`. Only
`CleanUpKeywords/Obsolete` is nullable, so omitting it stays a legal call that filters nothing out.

The `400` is `BadRequestODataResult`, not `ControllerBase.BadRequest`: only the OData result renders an
error payload. A `BadRequestObjectResult` holding a string comes back as an `Edm.String` *value* with a
`400` attached - a status code that contradicts its own body.
