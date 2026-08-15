-- Generated from the EF Core model - do not edit.
--
-- Regenerate after changing LibraryContext or the model classes:
--     dotnet run --project src/LibraryService -- --emit-schema ../../db/01-schema.sql
--
-- (the path is relative to the project directory, which is where `dotnet run` puts you)
--
-- Applied by Postgres before the service starts; the data that goes in is 02-seed.sql.

CREATE TABLE "Bookmobiles" (
    "Id" integer NOT NULL,
    "LicensePlate" character varying(12),
    "Route" text,
    "CurrentPosition" text,
    CONSTRAINT "PK_Bookmobiles" PRIMARY KEY ("Id")
);


CREATE TABLE "Branches" (
    "Id" integer NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Address_PostalCode" character varying(10),
    "Address_Country" character varying(60),
    "Address_Street" character varying(120),
    "Address_City" character varying(80),
    "Location" text,
    "CatchmentArea" text,
    "LowestFloor" smallint NOT NULL,
    "FloorPlanOrigin" text,
    "FloorPlanShapes" text,
    "OpensAt" time without time zone,
    "ClosesAt" time without time zone,
    "Amenities" integer,
    "Population" bigint NOT NULL,
    CONSTRAINT "PK_Branches" PRIMARY KEY ("Id")
);


CREATE TABLE "Contents" (
    "OwnerId" uuid NOT NULL,
    "Slot" integer NOT NULL,
    "Part" integer NOT NULL,
    "ContentType" text NOT NULL,
    "Bytes" bytea NOT NULL,
    CONSTRAINT "PK_Contents" PRIMARY KEY ("OwnerId", "Slot", "Part")
);


CREATE TABLE "IdDocuments" (
    "Id" uuid NOT NULL,
    "Scan" bytea,
    "UploadedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_IdDocuments" PRIMARY KEY ("Id")
);


CREATE TABLE "PublisherBranches" (
    "Id" integer NOT NULL,
    "City" character varying(80),
    "Country" character varying(60),
    CONSTRAINT "PK_PublisherBranches" PRIMARY KEY ("Id")
);


CREATE TABLE "Publishers" (
    "Id" integer NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Country" character varying(60),
    "Founded" date,
    CONSTRAINT "PK_Publishers" PRIMARY KEY ("Id")
);


CREATE TABLE "Members" (
    "Id" integer NOT NULL,
    "Name" character varying(100) NOT NULL,
    "DateOfBirth" date,
    "Address_PostalCode" character varying(10),
    "Address_Country" character varying(60),
    "Address_Street" character varying(120),
    "Address_City" character varying(80),
    "ActiveSince" timestamp with time zone,
    "Balance" numeric(9,2) NOT NULL,
    "IdDocumentId" uuid,
    "PreviousAddresses" jsonb,
    CONSTRAINT "PK_Members" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Members_IdDocuments_IdDocumentId" FOREIGN KEY ("IdDocumentId") REFERENCES "IdDocuments" ("Id") ON DELETE SET NULL
);


CREATE TABLE "Media" (
    "Id" uuid NOT NULL,
    "Title" character varying(200) NOT NULL,
    "Language" character varying(40),
    "PublicationDate" date,
    "Keywords" text[] NOT NULL,
    "PopularityScore" double precision,
    "MediumKind" character varying(21) NOT NULL,
    "Narrator" text,
    "Duration" interval,
    "ExtraData" json,
    "StorageLocationId" integer,
    "DynamicProperties" json,
    "RegionCode" smallint,
    "FileFormat" character varying(20),
    "ISBN" character varying(13),
    "PageCount" smallint,
    "AgeRating" smallint,
    "PublisherId" integer,
    "IssueNumber" integer,
    "Field" text,
    CONSTRAINT "PK_Media" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Media_Branches_StorageLocationId" FOREIGN KEY ("StorageLocationId") REFERENCES "Branches" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Media_Publishers_PublisherId" FOREIGN KEY ("PublisherId") REFERENCES "Publishers" ("Id") ON DELETE SET NULL
);


CREATE TABLE "Reservations" (
    "Id" uuid NOT NULL,
    "ReservedAt" timestamp with time zone NOT NULL,
    "MemberId" integer,
    CONSTRAINT "PK_Reservations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Reservations_Members_MemberId" FOREIGN KEY ("MemberId") REFERENCES "Members" ("Id") ON DELETE CASCADE
);


CREATE TABLE "AudiobookChapters" (
    "Id" integer NOT NULL,
    "AudiobookId" uuid NOT NULL,
    "Title" text,
    CONSTRAINT "PK_AudiobookChapters" PRIMARY KEY ("AudiobookId", "Id"),
    CONSTRAINT "FK_AudiobookChapters_Media_AudiobookId" FOREIGN KEY ("AudiobookId") REFERENCES "Media" ("Id") ON DELETE CASCADE
);


CREATE TABLE "Copies" (
    "MediumId" uuid NOT NULL,
    "InventoryNumber" integer NOT NULL,
    "Condition" smallint NOT NULL,
    "IsLoanable" boolean NOT NULL,
    "Status" smallint,
    "AcquisitionDate" date,
    "WeightKg" real NOT NULL,
    "Location_" character varying(10),
    "LocationId" integer,
    CONSTRAINT "PK_Copies" PRIMARY KEY ("MediumId", "InventoryNumber"),
    CONSTRAINT "FK_Copies_Branches_LocationId" FOREIGN KEY ("LocationId") REFERENCES "Branches" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Copies_Media_MediumId" FOREIGN KEY ("MediumId") REFERENCES "Media" ("Id") ON DELETE CASCADE
);


CREATE TABLE "Loans" (
    "Id" uuid NOT NULL,
    "LoanedAt" timestamp with time zone NOT NULL,
    "DueDate" date NOT NULL,
    "ReturnedAt" timestamp with time zone,
    "LateFee" numeric(5,2),
    "MemberId" integer,
    "CopyMediumId" uuid,
    "CopyInventoryNumber" integer,
    CONSTRAINT "PK_Loans" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Loans_Copies_CopyMediumId_CopyInventoryNumber" FOREIGN KEY ("CopyMediumId", "CopyInventoryNumber") REFERENCES "Copies" ("MediumId", "InventoryNumber") ON DELETE SET NULL,
    CONSTRAINT "FK_Loans_Members_MemberId" FOREIGN KEY ("MemberId") REFERENCES "Members" ("Id") ON DELETE CASCADE
);


CREATE INDEX "IX_Copies_LocationId" ON "Copies" ("LocationId");


CREATE INDEX "IX_Loans_CopyMediumId_CopyInventoryNumber" ON "Loans" ("CopyMediumId", "CopyInventoryNumber");


CREATE INDEX "IX_Loans_MemberId" ON "Loans" ("MemberId");


CREATE INDEX "IX_Media_PublisherId" ON "Media" ("PublisherId");


CREATE INDEX "IX_Media_StorageLocationId" ON "Media" ("StorageLocationId");


CREATE UNIQUE INDEX "IX_Members_IdDocumentId" ON "Members" ("IdDocumentId");


CREATE INDEX "IX_Reservations_MemberId" ON "Reservations" ("MemberId");


