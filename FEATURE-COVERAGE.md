# ASP.NET Core OData and the "Library" OData V4 test model

How far ASP.NET Core OData reproduces
[`model/library.xml`](https://github.com/odata2ts/test-reference-model/blob/main/model/library.xml), and
where it cannot.

Measured against **.NET 10.0.10** (the current LTS) with **Microsoft.AspNetCore.OData 9.5.0**. Every
statement below was verified against the emitted `$metadata` and against the running service - by
diffing the metadata mechanically, not by reading it.

The reference model is a deliberately feature-dense probe of the OData spec, not a benchmark. A server
does not have to implement all of OData. So this document separates *what the library cannot express* from
*what this implementation simply does not do*.

## Summary

The **protocol and operation surface is reproduced completely**: all 20 entity types, 9 complex types,
2 enums, 88 properties, 12 navigation properties, 10 entity sets, the singleton and all 29 operations -
including both overload pairs, which is the part most implementations lose.

What does not survive is **model metadata detail**: 14 attributes of the reference EDMX have no
equivalent in the model builder. None of them is exotic; they are `Partner`, `SRID`, `TypeDefinition`,
`Unicode` and two vocabulary annotations.

| Area                                    | Verdict                                                     |
| --------------------------------------- | ----------------------------------------------------------- |
| Entity types, inheritance, keys          | complete, three levels deep, composite key included          |
| Complex types incl. abstract base        | complete                                                     |
| Enums incl. flags and non-ASCII members  | complete                                                     |
| Operations (14 functions, 14 actions)    | complete, both overload pairs survive                        |
| Query options                            | complete, `$search` only with a hand-written binder           |
| Containment, media entities, open types  | complete                                                     |
| Model metadata detail                    | **14 attributes not expressible** - see below                |
| Vocabulary annotations                   | 2 of 4 - `Computed` and `OptimisticConcurrency` only          |

## What cannot be expressed at all

These are limits of `Microsoft.OData.ModelBuilder`, not of this implementation. Each was checked against
the library's API surface before being recorded here.

### `Partner` on navigation properties (6 occurrences)

The reference model relates both sides of an association - `Medium/Copies` ↔ `Copy/Medium`,
`Member/Loans` ↔ `Loan/Member`, `Publisher/Books` ↔ `Book/Publisher`.

`NavigationPropertyConfiguration.Partner` is **read-only**, and there is no API that relates two
navigation properties: no `WithMany`, no `WithRequired`, no `HasPartner`. The convention builder does not
infer it either, even though both sides are declared. The emitted model therefore has the navigation
properties but not the round trip between them, which costs a client the ability to navigate back
without consulting the entity sets.

`OnDelete="Cascade"` next to it *is* expressible (`CascadeOnDelete()`), and comes through.

### `SRID` on spatial properties (6 occurrences)

`Edm.GeographyPoint SRID="4326"` and `Edm.GeometryPoint SRID="0"` lose their SRID facet. There is no
`SRID` on any property configuration.

The spatial types themselves work, and the values are correct: `Branch.Location` serialises as GeoJSON
including `"crs": {"name": "EPSG:4326"}`, so the coordinate system survives *in the payload* while being
absent *from the metadata*. A client reading only the metadata cannot know it.

### `TypeDefinition`

`Library.Catalog.ISBN` - a named `Edm.String` with `MaxLength="13"` - has no equivalent. The builder has
no concept of a type definition; the property is emitted as plain `Edm.String`. The `MaxLength` facet is
kept, so nothing is lost semantically, but the named type is gone and with it the intent that ISBN is a
type rather than a string that happens to be short.

### `Unicode="false"`

`Copy/Location_` is declared non-unicode. There is no `IsUnicode` on any property configuration.

### `Core.AlternateKeys`

`PrintMedium` carries an alternate key on `ISBN`. The annotation is not emitted, and consequently
`GET /Media(ISBN='9783518188002')` answers **404** - verified. Addressing by alternate key is not
available.

### `Capabilities.SearchRestrictions`

Not emitted. This is metadata only: `$search` itself works, see below.

## What works, including the parts that usually do not

### Both overload pairs

The reference model contains two deliberate overload pairs, and **both survive into the metadata and are
callable**:

- `Search(Term)` and `Search(Term, MaxResults)` - same name, differing parameter count
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

## Two traps worth knowing

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
| `GET /Media(ISBN='…')` (alternate key)         | **404** |
| `GET /Media(<id>)/$value` (media entity stream) | **404** |
| `GET /Members(1)/Loans/$ref`                   | **404** |

## Not implemented here, though the library supports it

Kept separate from the list above on purpose - these are gaps in *this* service, not in ASP.NET Core
OData, and could be added:

- **Media entity streams.** `EBook` and `AudiobookChapter` are declared `HasStream`, and `Audiobook.Sample`
  is an `Edm.Stream` property, but no stream handler is wired up, so `$value` answers 404.
- **`$ref`.** Relationship management through `$ref` is not routed.
- **Deep insert** and **delta payloads** are untested.
