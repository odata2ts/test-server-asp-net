"use strict";

/**
 * httpyac configuration - turns the request collection into an assertable test run.
 *
 * Nothing is written into `test/*.http`: the files stay plain `.http`, readable by any client, and the
 * `### <status> - <note>` annotation they already carry stays the single statement of the expected result.
 * This hook reads that annotation back out and asserts against it, plus the curated body assertions in
 * test/harness/expectations.js.
 */

const assert = require("node:assert/strict");
const path = require("node:path");

const expectations = require("./test/harness/expectations");
const { parseRequests } = require("./test/harness/requests");

/** Parsed collection files, keyed by absolute path - each file is read once per run. */
const parsed = new Map();

function blocksOf(file) {
  let blocks = parsed.get(file);
  if (!blocks) {
    blocks = parseRequests(file);
    parsed.set(file, blocks);
  }
  return blocks;
}

/**
 * The body assertion declared for a request, if there is one.
 *
 * Matched on the request line as written, with `nth` counting identical lines in file order. lint.js
 * checks that every declaration resolves, so a miss here means the collection changed since the last lint
 * - which is worth failing over rather than skipping quietly.
 */
function expectationFor(file, block) {
  const declared = expectations[path.basename(file)] ?? [];
  const blocks = blocksOf(file).filter((candidate) => candidate.requestLine === block.requestLine);
  const occurrence = blocks.indexOf(block) + 1;

  return declared.find(
    (expectation) => expectation.request === block.requestLine && (expectation.nth ?? 1) === occurrence,
  );
}

/**
 * Runs an assertion so that its message survives into the report.
 *
 * httpyac builds the text it prints for a failed test by regex over the error's stack, which for an
 * AssertionError picks up a stack frame instead of the message. Dropping the stack makes it fall back to
 * the message itself - the assertion, not the line of this file that raised it.
 */
function reported(assertion) {
  return () => {
    try {
      assertion();
    } catch (error) {
      if (error instanceof Error) {
        error.stack = "";
      }
      throw error;
    }
  };
}

/** The response body as text, whatever the content type. */
function textOf(response) {
  if (typeof response.body === "string") {
    return response.body;
  }
  if (response.rawBody) {
    return response.rawBody.toString("utf8");
  }
  return response.body === undefined ? "" : JSON.stringify(response.body);
}

module.exports = {
  configureHooks: function (api) {
    api.hooks.onResponse.addHook("assertAnnotatedStatus", function (response, context) {
      const file = context.httpFile.fileName;
      const region = context.httpRegion.symbol;
      const block = blocksOf(file).find((candidate) => candidate.line === region.startLine);
      const test = api.utils.testFactory(context);

      // The response could not be matched back to a block. That is a defect in this harness rather than in
      // the server, and it has to fail loudly: silently skipping would mean the request was sent and its
      // result accepted unchecked.
      if (!block) {
        test(
          `${path.basename(file)}:${region.startLine + 1} annotation found`,
          reported(() =>
            assert.fail(
              `no ### annotation could be matched to the request at line ${region.startLine + 1} ` +
                `(${region.description}) - run "npm run lint:requests"`,
            ),
          ),
        );
        return;
      }

      const where = `${path.basename(file)}:${block.lineNumber}`;

      test(
        `${where} ${block.requestLine} -> ${block.status}`,
        reported(() =>
          assert.equal(
            response.statusCode,
            block.status,
            `expected ${block.status}${block.note ? ` (${block.note})` : ""}, got ` +
              `${response.statusCode} ${response.statusMessage ?? ""}`.trim(),
          ),
        ),
      );

      const expectation = expectationFor(file, block);
      if (expectation) {
        test(
          `${where} ${block.requestLine} body`,
          reported(() =>
            expectation.assert({
              body: response.parsedBody,
              text: textOf(response),
              response,
              assert,
            }),
          ),
        );
      }
    });
  },
};
