"use strict";

/**
 * Runs the request collection against the container image, one fresh container per file.
 *
 * The mutating files assume the freshly seeded state and are written to run top to bottom, and several of
 * them change or delete the same seed rows - `Members(1)` alone is patched by crud.http, rewritten by
 * batch.http, unlinked by refs.http and deleted by annotations.http. Integer keys make it worse: they are
 * assigned server-side as max + 1, so a single POST in one file shifts the keys another file addresses by
 * hand. There is no ordering that makes the files independent, so each gets its own container - which is
 * also the only reset this server has, and by design: a restart rebuilds the data directory from scratch.
 *
 * Measured at roughly four seconds a container locally, which is not what this run spends its time on.
 *
 *   IMAGE   image to run          (default test-server-asp-net:local)
 *   PORT    port to publish on    (default 5091, the port @host names in the files)
 */

const { spawn, spawnSync } = require("node:child_process");
const path = require("node:path");

const { collectionFiles } = require("./requests");

const ROOT = path.join(__dirname, "..", "..");
const IMAGE = process.env.IMAGE || "test-server-asp-net:local";
const PORT = Number(process.env.PORT || 5091);
const CONTAINER = "asp-net-http-test";
const READY_TIMEOUT_MS = 90_000;

function docker(args, options = {}) {
  return spawnSync("docker", args, { encoding: "utf8", ...options });
}

function removeContainer() {
  docker(["rm", "-f", CONTAINER], { stdio: "ignore" });
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Polls the service document rather than the container's health status, which only reports every 5s. */
async function waitUntilServing(url) {
  const deadline = Date.now() + READY_TIMEOUT_MS;

  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        await response.arrayBuffer();
        return;
      }
    } catch {
      // not listening yet
    }
    await sleep(200);
  }

  const logs = docker(["logs", "--tail", "40", CONTAINER]);
  throw new Error(`${IMAGE} did not answer on ${url} within ${READY_TIMEOUT_MS / 1000}s\n${logs.stdout}${logs.stderr}`);
}

/**
 * Reports one file's run.
 *
 * httpyac's own summary counts regions rather than requests - the `###` lines that separate the paragraphs
 * of a longer note open a region of their own - so the numbers worth reading are taken from the JSON
 * instead, and a failure is printed with the exchange that produced it.
 *
 * @returns {boolean} whether every test in the file passed
 */
function report(output) {
  let result;
  try {
    result = JSON.parse(output);
  } catch {
    console.log(output.trim());
    return false;
  }

  const requests = result.requests.filter((request) => request.testResults?.length > 0);
  const { totalTests, failedTests, erroredTests } = result.summary;

  for (const request of requests) {
    for (const test of request.testResults.filter((test) => test.status !== "SUCCESS")) {
      const response = request.response;

      console.log(`\n  ✖ ${test.message}`);
      console.log(`    ${test.error?.displayMessage ?? test.status}`);
      if (response) {
        console.log(`    ${response.request?.method} ${response.request?.url}`);
        console.log(`    ${response.statusCode} ${response.statusMessage ?? ""}`.trimEnd());
        if (response.body) {
          console.log(`    ${String(response.body).slice(0, 500)}`);
        }
      }
    }
  }

  const failures = failedTests + erroredTests;
  const summary = `${requests.length} requests, ${totalTests} assertions`;
  console.log(failures > 0 ? `\n  ${summary}, ${failures} failed` : `  ${summary}, all passed`);

  return failures === 0;
}

/** One file, against a container of its own. Resolves to true when every request in it passed. */
async function runFile(file, serviceUrl) {
  removeContainer();

  const started = docker(["run", "-d", "--name", CONTAINER, "-p", `${PORT}:4004`, IMAGE]);
  if (started.status !== 0) {
    throw new Error(`could not start ${IMAGE}: ${started.stderr.trim()}`);
  }

  try {
    await waitUntilServing(serviceUrl);

    const output = await new Promise((resolve, reject) => {
      const httpyac = spawn(
        process.execPath,
        [require.resolve("httpyac/bin/httpyac.js"), "send", "--all", "--json", path.relative(ROOT, file)],
        { cwd: ROOT, stdio: ["ignore", "pipe", "inherit"] },
      );

      let stdout = "";
      httpyac.stdout.on("data", (chunk) => (stdout += chunk));
      httpyac.on("error", reject);
      httpyac.on("close", () => resolve(stdout));
    });

    return report(output);
  } finally {
    removeContainer();
  }
}

async function main() {
  const requested = process.argv.slice(2);
  const files = requested.length > 0 ? requested.map((file) => path.resolve(file)) : collectionFiles();
  const serviceUrl = `http://localhost:${PORT}/odata/v4/library/`;

  if (docker(["version"], { stdio: "ignore" }).status !== 0) {
    console.error("docker is not available - the collection runs against the container image.");
    process.exitCode = 1;
    return;
  }

  if (docker(["image", "inspect", IMAGE], { stdio: "ignore" }).status !== 0) {
    console.error(`no image ${IMAGE}. Build it first:\n\n    docker build -t ${IMAGE} .\n`);
    process.exitCode = 1;
    return;
  }

  process.on("SIGINT", () => {
    removeContainer();
    process.exit(130);
  });

  console.log(`${files.length} file(s) against ${IMAGE}, a fresh container each, on port ${PORT}\n`);

  const failed = [];
  for (const file of files) {
    const name = path.basename(file);
    console.log(`── ${name} ${"─".repeat(Math.max(0, 60 - name.length))}`);

    if (!(await runFile(file, serviceUrl))) {
      failed.push(name);
    }
  }

  if (failed.length > 0) {
    console.error(`\n${failed.length} of ${files.length} file(s) failed: ${failed.join(", ")}`);
    process.exitCode = 1;
    return;
  }

  console.log(`\nAll ${files.length} file(s) passed.`);
}

main().catch((error) => {
  removeContainer();
  console.error(error.message);
  process.exitCode = 1;
});
