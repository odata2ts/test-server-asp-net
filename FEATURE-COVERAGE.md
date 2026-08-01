# ASP.NET Core OData and the "Library" OData V4 test model

How far ASP.NET Core OData reproduces
[`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml), and
where it cannot.

Measured against **.NET 10.0.10** (the current LTS) with **Microsoft.AspNetCore.OData 9.5.0**
(→ `Microsoft.OData.ModelBuilder` 2.0.0, `Microsoft.OData.Edm`/`Core` 8.4.0). Every statement below was
verified against the emitted `$metadata` and against the running service - by diffing the metadata
mechanically, not by reading it. Claims about what the *libraries* can or cannot do were checked against
their actual API surface by reflection and against isolated probe models, not inferred from this
service's behaviour.

The reference model is a deliberately feature-dense probe of the OData spec, not a benchmark. A server
does not have to implement all of OData. So this document separates *what the library cannot express* from
*what this implementation simply does not do*.

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
| Query options                            | complete, `$search` only with a hand-written binder           |
| Containment, media entities, open types  | complete, streams and `$ref` served in every position         |
| Navigation properties incl. `Partner`    | complete, both sides related, `OnDelete` intact              |
| Model metadata detail                    | **2 attributes not expressible**, 6 redundant - see below     |
| Vocabulary annotations                   | 4 of 4, alternate key addressable via the type cast          |

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

### `Core.OptimisticConcurrency`

Expressible via `[ConcurrencyCheck]`, emitted, and effective: `Copy` answers with `@odata.etag` in the
payload.

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

| Request                                       | Result |
| --------------------------------------------- | ------ |
| `$metadata`, service document                  | 200    |
| CRUD on entity sets (`POST`/`PATCH`/`DELETE`)  | 201 / 204 / 204 |
| Composite key `Copies(MediumId=…,InventoryNumber=…)` | 200 |
| Singleton `MainBranch`                         | 200    |
| `$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$expand`, `$count` | 200 |
| `$search` (with binder)                        | filters correctly |
| `$apply=groupby((Language))`                   | 200, groups correctly |
| `$compute`                                     | 200    |
| `$batch` (JSON)                                | 200    |
| Type-cast segment `/Media/Library.Catalog.Book` | 200   |
| Containment via type cast                      | 200    |
| All 14 functions, all 14 actions               | 200 / 201 / 204 as declared |
| `GET /Media/Library.Catalog.PrintMedium(ISBN='…')` (alternate key) | 200 |
| `GET /Media(ISBN='…')` (alternate key without the type cast) | 404 |
| `GET`/`PUT`/`DELETE` `/Media(<id>)/$value` (media entity stream) | 200 / 204 / 204 |
| `GET`/`PUT` `/Media(<id>)/Library.Catalog.Audiobook/Sample` (stream property) | 200 / 204 |
| `GET`/`PUT` `…/Chapters(<id>)/$value` (contained media entity) | 200 / 204 |
| `$ref` on a collection-valued navigation property | 200 / 204 |
| `$ref` on a single-valued navigation property  | 200 / 204 |
| Deep insert (`POST` with nested entities)      | 201, children addressable in their own set |
| Delta payload (`PATCH` on the collection)      | 200, update + removal + upsert applied |
| `@odata.bind` / `{"@id"}` on create and update | 201 / 204, link re-pointed, store intact |
| binding to `null`                              | 204, link cleared |

### Media entity streams

All three positions the reference model puts a stream in are served, read and write:

- `EBook` - a media entity *inside* the inheritance hierarchy
- `AudiobookChapter` - a media entity that is at the same time a *contained* entity, reached as
  `…/Library.Catalog.Audiobook/Chapters(<id>)/$value`
- `Audiobook.Sample` - a stream *property* rather than an entity's content

The content type given on `PUT` is stored and returned on the next `GET`. An entity that exists but has
no content yet answers `204`, not `404` - the distinction matters to a client deciding whether to upload.

### `$ref`

Both cardinalities are served, and they differ in the verbs as the spec requires: a collection-valued
navigation property takes `POST` to add and `DELETE` with `$id` to remove, a single-valued one takes `PUT`
to set and plain `DELETE` to clear. A reference to a non-existent entity is refused with `400`.

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

Nothing outstanding: every feature the reference model declares is served, and every attribute it
declares is emitted except the two the model builder cannot express (`TypeDefinition`, `Unicode`) and the
six redundant `SRID` facets - all of them above, with the reasoning.

Two deviations from the reference EDMX are deliberate and both are forced from below, not chosen:

- the alternate key's `PropertyRef` carries an `Alias`, because ODL throws without one
- addressing by that key needs the type cast (`/Media/Library.Catalog.PrintMedium(ISBN='…')`), because
  `ISBN` is declared on `PrintMedium` while the entity set is of `Medium` - which is what the spec asks
  for, and the same shape containment already has here
