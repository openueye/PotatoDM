import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

test("browser callbacks preserve results, errors and the storage receiver", async () => {
  const chrome = { runtime: { lastError: null } };
  const context = { chrome };
  runInNewContext(
    readFileSync(new URL("../shared/browser-api.js", import.meta.url), "utf8"),
    context,
  );
  const { api, call, callStorage } = context.PotatoDMBrowser;
  assert.equal(api, chrome);
  assert.equal(await call((value, done) => done(value + 1), 2), 3);

  await assert.rejects(call((done) => {
    chrome.runtime.lastError = { message: "not available" };
    done();
    chrome.runtime.lastError = null;
  }), /not available/);

  const area = {
    value: 7,
    get(key, done) { done(this[key]); },
  };
  assert.equal(await callStorage(area, "get", "value"), 7);
});
