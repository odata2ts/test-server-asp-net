"use strict";

/**
 * Static check over the request collection - no server involved.
 *
 * The whole harness rests on one property: every request carries a `### <status>` annotation on the first
 * line of its block. A request without one would be sent and its result silently accepted, which is the one
 * way this could quietly stop testing anything. So it is checked rather than assumed.
 *
 * It also resolves every curated body assertion against the collection, so that a renamed or deleted
 * request fails here with a clear message instead of never running again unnoticed.
 */

const path = require("node:path");

const expectations = require("./expectations");
const { collectionFiles, parseRequests } = require("./requests");

function main() {
  const problems = [];
  let requests = 0;

  for (const file of collectionFiles()) {
    const name = path.basename(file);
    const blocks = parseRequests(file);
    requests += blocks.length;

    for (const block of blocks) {
      if (block.annotation.length === 0) {
        problems.push(`${name}:${block.lineNumber}  no ### annotation above  ${block.requestLine}`);
      } else if (block.status === null) {
        problems.push(
          `${name}:${block.lineNumber}  first annotation line does not start with a status code: ` +
            `"### ${block.annotation[0]}"`,
        );
      }
    }

    for (const expectation of expectations[name] ?? []) {
      const matches = blocks.filter((block) => block.requestLine === expectation.request);

      if (matches.length === 0) {
        problems.push(`${name}  body assertion targets a request that is not in the file: ${expectation.request}`);
      } else if (matches.length > 1 && expectation.nth === undefined) {
        problems.push(
          `${name}  body assertion is ambiguous: ${matches.length} requests read ${expectation.request} ` +
            `(lines ${matches.map((match) => match.lineNumber).join(", ")}) - name one with nth`,
        );
      } else if (expectation.nth !== undefined && (expectation.nth < 1 || expectation.nth > matches.length)) {
        problems.push(
          `${name}  body assertion asks for occurrence ${expectation.nth} of ${expectation.request}, ` +
            `of which the file has ${matches.length}`,
        );
      }
    }
  }

  for (const name of Object.keys(expectations)) {
    if (!collectionFiles().some((file) => path.basename(file) === name)) {
      problems.push(`body assertions are declared for ${name}, which is not in the collection`);
    }
  }

  if (problems.length > 0) {
    console.error(`${problems.length} problem(s) in the request collection:\n`);
    for (const problem of problems) {
      console.error(`  ${problem}`);
    }
    process.exitCode = 1;
    return;
  }

  const asserted = Object.values(expectations).reduce((sum, list) => sum + list.length, 0);
  console.log(`${requests} requests, all annotated; ${asserted} body assertions resolve.`);
}

main();
