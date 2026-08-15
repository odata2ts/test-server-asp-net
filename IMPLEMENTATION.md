# How this server is built

Everything the "Library" model costs in code: the places where a convention does not do, the workarounds a
library defect forces, and what the persistence layer demands. The *result* of all this - which OData
features are covered and which are not - is in [FEATURE-COVERAGE.md](FEATURE-COVERAGE.md); this file is the
reasoning behind the rows that carry an **impl** tick.

Measured against **.NET 10.0.10** with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0) and
**Microsoft.EntityFrameworkCore.Sqlite 10.0.11**.

## The EDM model

### `Namespace` drags every type with it

`ODataConventionModelBuilder.Namespace` names the entity container. It also becomes the namespace of every
type, so a model with four schemas silently collapses into one. The types have to be put back explicitly
from their CLR namespaces afterwards (`AlignNamespacesWithClrTypes`).

Enums are worse: one reached only through a property is registered so late that a namespace fix-up misses
it. They have to be registered explicitly with `EnumType<T>()` first.

### `Partner` on both sides of every association

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

### Binding parameter names

The builder names every binding parameter `bindingParameter`. That breaks `EntitySetPath="medium/Copies"`,
which refers to the parameter by name. `SetBindingParameter(name, type)` fixes it, so the model uses the
reference names (`medium`, `member`, `copy`, `loan`, `loans`, `media`).

### Overload pairs

`Search(Term)` / `Search(Term, MaxResults)` are both callable, but only one endpoint is ever registered:
OData resolves the function by name, so a second action with the longer route template is never selected
and its extra parameter stays unbound. One action serves both overloads and reads `MaxResults` off the URL.
`AvailableCopies`, bound once to a single `Medium` and once to a `Collection(Medium)`, needs nothing
special.

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

The store is SQLite, held in memory, through EF Core. Against a database the query options have to become
SQL, which is what a consumer of a real OData service meets. The SQLite connection is opened once in
`Program.cs` and **never closed**: an in-memory database lives exactly as long as a connection to it is
open.

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
type. Scaling to an integer keeps the value exact and both work, and it turns the `Precision`/`Scale`
facets the model already declares into the converter's contract. It is visible in one payload: the
converter reconstructs at the declared `Scale`, so `Member.Balance` serialises a whole value as `0.00`
rather than `0`. `DateTimeOffset` and `Duration` went the same way for the same reason, except that EF
refused to translate those outright instead of answering wrongly - and there the trade is not free, since
the extraction functions go with it.

`LoanedAt` is stored as an integer tick count, and no SQL pulls an hour back out of one. The alternative is
worse rather than better, and was measured rather than assumed: with EF's default mapping **every one** of
those requests fails, comparison and `$orderby` included, and the timestamps serialise differently as well.
Ticks buy the operators the reference model exercises and cost the extraction functions. `Edm.Date` is
unaffected - `year(PublicationDate)` works - because a date needs no converter.

### The seed

Every key in this model is caller-assigned - fixed GUIDs in the seed, `max(Id) + 1` for members - because a
test server whose keys depend on insert order is one nobody can assert against.

Collection order without `$orderby` is not promised by OData, but consumers had a stable one, and EF groups
the inserts of one `SaveChanges` by entity type, which permuted `/Media` into alphabetical order by CLR
type. [`LibrarySeed`](src/LibraryService/Data/LibrarySeed.cs) therefore inserts media and copies one at a
time to preserve it.

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
$filter=PublicationDate gt 2000-01-01  ->  WHERE CAST(strftime('%Y', "PublicationDate") AS INTEGER) * 10000
                                               + CAST(strftime('%m', …) AS INTEGER) * 100
                                               + CAST(strftime('%d', …) AS INTEGER) > @p

$filter=OpensAt gt 09:30:00            ->  (long)"OpensAt".Hour * 36000000000 + …   no translation, 500
```

Over `List<T>` that is merely roundabout. Over a database the date form still answers correctly but can
never use an index - the column never appears on its own - and the time-of-day form has no SQL at all, so
the request failed outright. Not a storage problem: converting the column to ticks was tried first and does
not help, because the arithmetic is built from `.Hour`/`.Minute` before the provider ever sees it.

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
wrong answer rather than an error:

```
$filter=date(LoanedAt) eq 2026-06-01   ->   no match, for a loan that is plainly on that date
```

The library's `date()` does not truncate - it hands the whole timestamp through, which
`$compute=date(LoanedAt) as D` makes visible by returning `2026-06-01T10:00:00Z`. The other operand is
therefore a `DateTimeOffset`, not a `DateOnly`, and the binder used to convert the `Edm.Date` literal to
match it - producing midnight, and comparing 10:00 against 00:00.

So `Constant` accepts **only** `DateOnly` for `Edm.Date` and `TimeOnly` for `Edm.TimeOfDay`, and returns
null for anything else, which hands the comparison back to the base implementation - whose part-by-part
arithmetic is roundabout but correct. The binder takes over only what it can state more directly.

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

### Patching an entity set whose declared type is abstract

`Media` is declared as `Library.Catalog.Medium`, which is abstract, so every entity in it is of a derived
type - and OData JSON requires the `@odata.type` annotation whenever an instance's type is derived from the
declared one. A partial update therefore has to name the type it is patching:

```
PATCH /Media(<id>)   {"Title": "Neu"}                                          400
PATCH /Media(<id>)   {"@odata.type": "#Library.Catalog.Book", "Title": "Neu"}  204
```

The 400 is this implementation's, not the library's. Without the annotation the deserializer has no type to
construct and model binding hands the action a **null** delta, with no error of its own - so the shape of
the failure is a choice each implementation makes. Dereferencing it answers 500 to what is really a
malformed request; skipping it silently answers 204 to an update that was never applied, which is worse.
All five `PATCH` actions here take a nullable delta and answer 400, except where a null delta is still
usable: on `Copy` and `Member` the deserializer also rejects a body that binds a navigation to null, and
those two read the binding out of the raw body themselves, so they answer 400 only when there is no binding
either.

Worth knowing when generating a client: a `PATCH` builder that emits only the changed properties produces
the first shape, and against this entity set that is not a valid payload. The other entity sets are
declared with concrete types and take an untyped body.

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
