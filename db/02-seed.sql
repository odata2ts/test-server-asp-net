-- The fixed seed data of the "Library" test server.
--
-- Applied by Postgres once, on an empty data directory, before the service is reachable - so every
-- container starts from exactly this state and a restart is a reset. Consumers assert against these keys,
-- so nothing here may become time-dependent, random or insert-order-dependent:
--
--     Media          11111111-… Der Prozess (Book)     55555555-… Journal of Library Science
--                    22222222-… Die Verwandlung        66666666-… Metropolis (DVD)
--                    33333333-… Digitale Aufklärung    77777777-… Erstausgabe 1899 (open type)
--                    44444444-… Stadtmagazin
--     Loan           88888888-…        Reservation  99999999-…        IdDocument  aaaaaaaa-…
--     Members        1 Alice Muster, 2 Bob Beispiel
--     Branches       1 Central Library, 2 Suburban Branch
--
-- Statement order is load-bearing twice over. Across tables it satisfies the foreign keys, which is why
-- the referenced rows come first. Within a table it fixes the physical row order, and that is what a
-- consumer sees from an unordered request - `/Media` without `$orderby` answers in the order the seven
-- INSERTs below appear. Nothing in OData promises that, but it has been stable across every version of
-- this server, so it is pinned deliberately rather than left to chance.
--
-- Two columns hold a representation rather than a value, both explained in ValueConversions.cs: the
-- spatial ones are WKT with an SRID prefix, and the open type's DynamicProperties/ExtraData are jsonb.
-- The literals below are exactly what the mapping round-trips - do not reformat them.

INSERT INTO "Publishers" ("Id", "Name", "Country", "Founded") VALUES
  (1, 'Suhrkamp', 'DE', '1950-07-01'),
  (2, 'Penguin', 'GB', '1935-07-30');

-- Same short name as Library.Circulation.Branch, different EDM namespace - and a second table here.
INSERT INTO "PublisherBranches" ("Id", "City", "Country") VALUES
  (1, 'Berlin', 'DE'),
  (2, 'London', 'GB');

-- Amenities is the flags enum: 5 = WheelchairAccessible | Café, 10 = Parking | KidsArea.
INSERT INTO "Branches" (
  "Id", "Name",
  "Address_Street", "Address_City", "Address_PostalCode", "Address_Country",
  "Location", "CatchmentArea", "FloorPlanOrigin", "FloorPlanShapes",
  "LowestFloor", "OpensAt", "ClosesAt", "Amenities", "Population") VALUES
  (1, 'Central Library',
   'Hauptstraße 1', 'Berlin', '10115', 'DE',
   'SRID=4326;POINT (13.405 52.52)', NULL, NULL, NULL,
   -2, '09:00:00', '20:00:00', 5, 3600000),
  (2, 'Suburban Branch',
   NULL, NULL, NULL, NULL,
   'SRID=4326;POINT (13.32 52.48)', NULL, NULL, NULL,
   0, '10:00:00', '18:00:00', 10, 120000);

INSERT INTO "Bookmobiles" ("Id", "LicensePlate", "Route", "CurrentPosition") VALUES
  (1, 'B-LIB-1', NULL, 'SRID=4326;POINT (13.39 52.51)');

-- Before Members: the member references the document, not the other way round.
INSERT INTO "IdDocuments" ("Id", "Scan", "UploadedAt") VALUES
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '\x01020304', '2015-01-05 09:05:00+00');

-- PreviousAddresses is a collection of a complex type, stored as a JSON array; Alice has one, Bob none.
INSERT INTO "Members" (
  "Id", "Name", "DateOfBirth",
  "Address_Street", "Address_City", "Address_PostalCode", "Address_Country",
  "ActiveSince", "Balance", "IdDocumentId", "PreviousAddresses") VALUES
  (1, 'Alice Muster', '1988-04-12',
   'Lindenweg 4', 'Berlin', '10115', 'DE',
   '2015-01-05 09:00:00+00', 12.50, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
   '[{"City": "Potsdam", "Street": "Alte Gasse 9", "Country": "DE", "PostalCode": "14467"}]'),
  (2, 'Bob Beispiel', '1975-11-30',
   NULL, NULL, NULL, NULL,
   '2020-06-20 14:30:00+00', 0.00, NULL,
   '[]');

-- One table for the whole hierarchy - Medium -> PrintMedium -> Magazine -> TradeJournal and
-- Medium -> AudioMedium -> Audiobook - told apart by MediumKind. The order of these seven rows is the
-- order an unordered /Media answers in.
INSERT INTO "Media" (
  "Id", "MediumKind", "Title", "Language", "PublicationDate", "Keywords", "PopularityScore",
  "ISBN", "PageCount", "AgeRating", "PublisherId",
  "IssueNumber", "Field",
  "Duration", "Narrator", "RegionCode", "FileFormat",
  "ExtraData", "DynamicProperties", "StorageLocationId") VALUES
  ('11111111-1111-1111-1111-111111111111', 'Book', 'Der Prozess', 'de', '1925-04-26',
   '{Roman,Klassiker,Fragment}', 9.1,
   '9783518188002', 320, 16, 1,
   NULL, NULL,
   NULL, NULL, NULL, NULL,
   NULL, NULL, NULL),
  ('22222222-2222-2222-2222-222222222222', 'Audiobook', 'Die Verwandlung (Hörbuch)', 'de', '2015-03-01',
   '{Hörbuch,Klassiker}', 7.4,
   NULL, NULL, NULL, NULL,
   NULL, NULL,
   '01:52:00', 'Anna Beispiel', NULL, NULL,
   NULL, NULL, NULL),
  ('33333333-3333-3333-3333-333333333333', 'EBook', 'Digitale Aufklärung', 'de', '2021-09-15',
   '{Sachbuch}', 5.2,
   NULL, NULL, NULL, NULL,
   NULL, NULL,
   NULL, NULL, NULL, 'EPUB',
   NULL, NULL, NULL),
  ('44444444-4444-4444-4444-444444444444', 'Magazine', 'Stadtmagazin', 'de', '2026-01-01',
   '{}', 3,
   '9770000000001', NULL, NULL, NULL,
   142, NULL,
   NULL, NULL, NULL, NULL,
   NULL, NULL, NULL),
  ('55555555-5555-5555-5555-555555555555', 'TradeJournal', 'Journal of Library Science', 'en', '2025-11-01',
   '{}', 4.5,
   NULL, NULL, NULL, NULL,
   12, 'Information Science',
   NULL, NULL, NULL, NULL,
   NULL, NULL, NULL),
  ('66666666-6666-6666-6666-666666666666', 'DVD', 'Metropolis', 'de', '1927-01-10',
   '{}', 8,
   NULL, NULL, NULL, NULL,
   NULL, NULL,
   '02:33:00', NULL, 2, NULL,
   NULL, NULL, NULL),
  ('77777777-7777-7777-7777-777777777777', 'CollectorsItem', 'Erstausgabe 1899', 'de', NULL,
   '{}', 9.9,
   NULL, NULL, NULL, NULL,
   NULL, NULL,
   NULL, NULL, NULL, NULL,
   -- Key order is preserved verbatim (the column is json, not jsonb) and is the order these appear in
   -- the payload, because OData writes the dynamic properties in the order they materialise.
   '"provenance unknown"', '{"Appraisal": 12500, "Insured": true}', 1);

-- Contained entities: Id is unique per audiobook, not globally, so the parent's key is half of theirs.
INSERT INTO "AudiobookChapters" ("AudiobookId", "Id", "Title") VALUES
  ('22222222-2222-2222-2222-222222222222', 1, 'Erwachen'),
  ('22222222-2222-2222-2222-222222222222', 2, 'Der Apfel');

-- Status is the byte-backed enum: 0 Available, 1 OnLoan, 2 InRepair. Condition is the concurrency token.
INSERT INTO "Copies" (
  "MediumId", "InventoryNumber", "Condition", "IsLoanable", "Status",
  "AcquisitionDate", "WeightKg", "Location_", "LocationId") VALUES
  ('11111111-1111-1111-1111-111111111111', 1, 2, true, 1, '2019-05-02', 0.42, 'A-12', 1),
  ('11111111-1111-1111-1111-111111111111', 2, 1, true, 0, '2022-08-17', 0.41, 'A-13', 1),
  ('66666666-6666-6666-6666-666666666666', 1, 3, false, 2, '2018-02-01', 0.09, 'C-01', 2);

INSERT INTO "Loans" (
  "Id", "LoanedAt", "DueDate", "ReturnedAt", "LateFee",
  "MemberId", "CopyMediumId", "CopyInventoryNumber") VALUES
  ('88888888-8888-8888-8888-888888888888', '2026-06-01 10:00:00+00', '2026-07-01', NULL, 2.50,
   1, '11111111-1111-1111-1111-111111111111', 1);

INSERT INTO "Reservations" ("Id", "ReservedAt", "MemberId") VALUES
  ('99999999-9999-9999-9999-999999999999', '2026-07-20 08:15:00+00', 1);

-- The bytes behind Edm.Stream, deliberately not part of the EDM - a stream is a link in the payload and
-- never an inline value. Slot: 0 the entity's own content, 1 Audiobook.Sample, 2 a chapter (Part = its Id).
INSERT INTO "Contents" ("OwnerId", "Slot", "Part", "ContentType", "Bytes") VALUES
  ('33333333-3333-3333-3333-333333333333', 0, 0, 'application/epub+zip',
   '\x4550554220706c616365686f6c64657220666f72207465737473'),  -- 'EPUB placeholder for tests'
  ('22222222-2222-2222-2222-222222222222', 1, 0, 'audio/mpeg',
   '\x73616d706c6520617564696f'),                              -- 'sample audio'
  ('22222222-2222-2222-2222-222222222222', 2, 1, 'audio/mpeg',
   '\x63686170746572206f6e6520617564696f'),                    -- 'chapter one audio'
  ('22222222-2222-2222-2222-222222222222', 2, 2, 'audio/mpeg',
   '\x636861707465722074776f20617564696f');                    -- 'chapter two audio'
