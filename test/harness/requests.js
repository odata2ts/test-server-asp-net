"use strict";

/**
 * Reads the `### <status> - <note>` annotations out of the request collection.
 *
 * The annotation is what the collection has always carried: the status code and the behaviour actually
 * observed against the running server. Deriving the assertions from it rather than writing them into the
 * files keeps `test/*.http` plain `.http` - openable in the VS Code REST Client or the IntelliJ HTTP
 * client, with no runner-specific syntax - and keeps one statement of the expected result instead of two
 * that can disagree.
 */

const fs = require("node:fs");
const path = require("node:path");

/**
 * An annotation line: `###` followed by whitespace, or a bare `###` - the blank line a longer note uses to
 * separate its paragraphs. The section banners in the files are `###...###` with no space, so they do not
 * match and cannot be mistaken for one.
 */
const ANNOTATION = /^###(\s|$)/;

/** The status code has to be the first thing on the first annotation line of a block. */
const STATUS = /^###\s+(\d{3})\b\s*-?\s*(.*)$/;

/**
 * A request line. Only recognised at the start of a line, and only outside a request body - see
 * `parseRequests`. Anchoring alone is not enough: a *multipart* `$batch` states its parts as
 * `GET Books?$top=1 HTTP/1.1` at column zero, which is indistinguishable from a request of its own.
 */
const REQUEST_LINE = /^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+\S/;

/** A variable definition, `@host = ...`. Ends a body, since nothing of a request follows it. */
const VARIABLE = /^@\w/;

/**
 * @typedef {object} RequestBlock
 * @property {string} file            path as given
 * @property {number} line            zero-based index of the request line - how a response is matched back
 * @property {number} lineNumber      one-based, for messages
 * @property {string} requestLine     the request line verbatim, variables unresolved
 * @property {string[]} annotation    the `###` lines above it, `###` stripped
 * @property {number|null} status     the status code off the first annotation line, null if there is none
 * @property {string} note            the rest of that first line
 */

/**
 * Every request in one `.http` file, with the annotation that belongs to it.
 *
 * A block is a run of `###` lines immediately above a request line. Anything else - the file header, the
 * section banners, the `@variable` definitions - is not a block and is ignored.
 *
 * Request bodies are skipped rather than scanned. A body starts at the blank line after a request line
 * and runs until the next `###` block or `@variable`, and nothing inside it can open a request - which is
 * what keeps the parts of a multipart `$batch` from being read as requests of their own. httpyac draws the
 * same boundary, so the blocks here stay aligned with the regions it reports.
 *
 * @param {string} file
 * @returns {RequestBlock[]}
 */
function parseRequests(file) {
  const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
  const blocks = [];
  let inBody = false;

  for (let i = 0; i < lines.length; i++) {
    if (ANNOTATION.test(lines[i]) || VARIABLE.test(lines[i])) {
      inBody = false;
    }

    if (inBody || !REQUEST_LINE.test(lines[i])) {
      continue;
    }

    // Everything from the blank line after this request until the next block is its body.
    inBody = true;

    let start = i;
    while (start > 0 && ANNOTATION.test(lines[start - 1])) {
      start--;
    }

    const annotation = lines.slice(start, i).map((line) => line.replace(/^###\s?/, ""));
    const status = annotation.length > 0 ? STATUS.exec(lines[start]) : null;

    blocks.push({
      file,
      line: i,
      lineNumber: i + 1,
      requestLine: lines[i].trim(),
      annotation,
      status: status ? Number(status[1]) : null,
      note: status ? status[2].trim() : "",
    });
  }

  return blocks;
}

/** The collection, in the order the runner walks it. */
function collectionFiles(dir = path.join(__dirname, "..")) {
  return fs
    .readdirSync(dir)
    .filter((name) => name.endsWith(".http"))
    .sort()
    .map((name) => path.join(dir, name));
}

module.exports = { parseRequests, collectionFiles };
