"use strict";

/**
 * Curated response-body assertions.
 *
 * The status code of every request is asserted from its `### <status>` annotation - see requests.js. That
 * catches a route that stops binding, a 400 that turns into a 500, a workaround that quietly stopped
 * working. It does not catch a payload that changes shape while still answering 200, so the handful of
 * responses whose *content* is the point are pinned here.
 *
 * Deliberately not a snapshot of all 244 responses: the server is still moving, and a golden file would
 * need scrubbing rules for server-assigned keys, ETags and result order before it recorded anything. These
 * are picked instead for being stable by construction - scalars, counts, `$orderby`-pinned lists, and
 * round-trips that read back what the preceding request wrote.
 *
 * Keys are the request line as written in the file. `nth` picks one of several identical lines, counting
 * from 1 in file order; lint.js insists on it wherever the line is not unique, so an assertion can never
 * silently move to a different request.
 *
 * Each assertion gets `{ body, text, response, assert }`: `body` is the parsed JSON (undefined when the
 * response is not JSON), `text` the raw body, `assert` node's strict assert.
 */

/** `<Annotation Term="…"/>` elements in an EDMX document, and the distinct terms they use. */
function annotations(edmx) {
  const terms = [...edmx.matchAll(/<Annotation\s+Term="([^"]+)"/g)].map((match) => match[1]);
  return { count: terms.length, terms: new Set(terms) };
}

module.exports = {
  "service.http": [
    {
      request: "GET {{host}}/",
      // The service document is the entry point a generated client reads first: if a set or a function
      // import silently leaves it, every client built from it loses that set.
      assert: ({ body, assert }) => {
        assert.deepEqual(
          body.value.map((entry) => entry.name),
          [
            "Media",
            "Copies",
            "Members",
            "Loans",
            "Reservations",
            "IdDocuments",
            "Branches",
            "Bookmobiles",
            "Publishers",
            "PublisherBranches",
            "MainBranch",
            "TotalMediaCount",
            "AllLanguages",
            "StatsPerBranch",
            "MostReadMedium",
            "NewReleases",
          ],
        );
      },
    },
    {
      request: "GET {{host}}/$metadata",
      // Four schemas, and the second Branch entity type that makes PublisherRegistry worth having: a
      // client generator that collapses namespaces breaks exactly here.
      assert: ({ text, assert }) => {
        const schemas = [...text.matchAll(/<Schema\s+Namespace="([^"]+)"/g)].map((match) => match[1]);
        assert.deepEqual(schemas, ["Library.Catalog", "Library.Circulation", "PublisherRegistry", "Library.Service"]);
        assert.match(text, /<EntityType\s+Name="Branch"/);
      },
    },
  ],

  "annotations.http": [
    {
      request: "GET {{host}}/$metadata",
      // The headline claim of the file, in one number each. The individual shapes are covered by the
      // requests below it; these two guard against the emitter losing a whole group unnoticed.
      assert: ({ text, assert }) => {
        const { count, terms } = annotations(text);
        assert.equal(count, 69);
        assert.equal(terms.size, 24);
      },
    },
  ],

  "read.http": [
    {
      request: "GET {{host}}/Media",
      // The set is declared as the abstract Medium, so every instance has to name its derived type.
      assert: ({ body, assert }) => {
        assert.equal(body.value.length, 7);
        for (const medium of body.value) {
          assert.match(medium["@odata.type"], /^#Library\.Catalog\./);
        }
      },
    },
  ],

  "crud.http": [
    {
      request: "GET {{host}}/Branches?$select=Id,Name&$orderby=Id",
      // Read right after a create that *bound* a branch. A binding gone wrong does not announce itself in
      // the status code: the create answers 201 either way, and what is left behind is a branch carrying
      // someone else's name - or, with the graph tracked as Added throughout, a second row under the same
      // key. Both are only visible here.
      assert: ({ body, assert }) => {
        assert.deepEqual(
          body.value.map((branch) => [branch.Id, branch.Name]),
          [
            [1, "Central Library"],
            [2, "Suburban Branch"],
          ],
        );
      },
    },
  ],

  "query-options.http": [
    {
      request: "GET {{host}}/Media?$orderby=Title&$top=2&$count=true",
      // $count counts the set, $top only limits the page - and the order is byte order, which is why the
      // container initialises Postgres with locale=C.
      assert: ({ body, assert }) => {
        assert.equal(body["@odata.count"], 7);
        assert.deepEqual(
          body.value.map((medium) => medium.Title),
          ["Der Prozess", "Die Verwandlung (Hörbuch)"],
        );
      },
    },
  ],

  "operations.http": [
    {
      request: "GET {{host}}/TotalMediaCount()",
      assert: ({ body, assert }) => assert.equal(body.value, 7),
    },
    {
      request: "GET {{host}}/AllLanguages()",
      assert: ({ body, assert }) => assert.deepEqual(body.value, ["de", "en"]),
    },
    {
      request: "GET {{host}}/Members(1)/Library.Circulation.OutstandingBalance()",
      // Edm.Decimal, and the scale has to survive the trip: 12.50, not 12.5 rounded off somewhere.
      assert: ({ body, text, assert }) => {
        assert.equal(body.value, 12.5);
        assert.match(text, /"value":\s*12\.50\b/);
      },
    },
  ],

  "streams.http": [
    {
      request: "GET {{host}}/Media({{ebook}})/$value",
      nth: 1,
      assert: ({ text, response, assert }) => {
        assert.equal(text.trim(), "EPUB placeholder for tests");
        assert.equal(response.contentType?.mimeType, "application/epub+zip");
      },
    },
    {
      request: "GET {{host}}/Media({{ebook}})/$value",
      nth: 2,
      // Reads back what the PUT above wrote, content type included - the round-trip is the feature.
      assert: ({ text, response, assert }) => {
        assert.equal(text.trim(), "neuer inhalt");
        assert.equal(response.contentType?.mimeType, "application/epub+zip");
      },
    },
    {
      request: "GET {{host}}/Media({{audiobook}})/Library.Catalog.Audiobook/Sample",
      nth: 2,
      // The same for a stream property behind a type cast.
      assert: ({ text, assert }) => assert.equal(text.trim(), "neues sample"),
    },
  ],

  "refs.http": [
    {
      request: "GET {{host}}/Members(1)/Loans/$ref",
      nth: 1,
      // Collection-valued: a value array of @odata.id, one per link.
      assert: ({ body, assert }) => {
        assert.equal(body.value.length, 1);
        assert.match(body.value[0]["@odata.id"], /\/Loans\(88888888-8888-8888-8888-888888888888\)$/);
      },
    },
    {
      request: "GET {{host}}/Members(1)/IdDocument/$ref",
      nth: 1,
      // Single-valued: one @odata.id at the top level, not a collection of one.
      assert: ({ body, assert }) => {
        assert.equal(body.value, undefined);
        assert.match(body["@odata.id"], /\/IdDocuments\(aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\)$/);
      },
    },
  ],

  "batch.http": [
    {
      request: "POST {{host}}/$batch",
      nth: 1,
      // The outer status is 200 whatever happens inside, so the sub-responses are the only thing worth
      // asserting: three of them, and the third has to see what the second wrote.
      assert: ({ body, assert }) => {
        assert.deepEqual(
          body.responses.map((response) => response.id),
          ["1", "2", "3"],
        );
        assert.deepEqual(
          body.responses.map((response) => response.status),
          [200, 204, 200],
        );
        assert.equal(body.responses[2].body.Name, "Batch Alice");
      },
    },
    {
      request: "POST {{host}}/$batch",
      nth: 2,
      // A failing sub-request stays a failing sub-request; it does not take the batch with it.
      assert: ({ body, assert }) => {
        assert.deepEqual(
          body.responses.map((response) => response.status),
          [404, 200],
        );
      },
    },
  ],
};
