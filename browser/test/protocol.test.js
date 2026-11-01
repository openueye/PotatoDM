import { test } from "node:test";
import assert from "node:assert/strict";

import "../shared/protocol.js";

const { NATIVE_TIMEOUT_MS, PROTOCOL_VERSION, HOST_NAME, handOffKind } =
  globalThis.PotatoDMProtocol;

// ---------------------------------------------------------------------------
// The timeout chain (G1-b)
//
// Three timeouts have to nest, and the extension's is the outermost one. If it
// fires first the extension re-downloads through the browser while the desktop
// app is still working on the same file, and the user gets it twice.

const HOST_STARTUP_BUDGET_MS = 15000; // src/PotatoDM.BrowserHost/Program.cs:16 DefaultBudget
const APP_PROBE_TIMEOUT_MS = 8000; // DownloadManager ctor: browserProbeTimeout default
const MARGIN_MS = 2000;

test("the extension waits longer than the host plus the app's probe", () => {
  const required = HOST_STARTUP_BUDGET_MS + APP_PROBE_TIMEOUT_MS + MARGIN_MS;

  assert.ok(
    NATIVE_TIMEOUT_MS >= required,
    `NATIVE_TIMEOUT_MS is ${NATIVE_TIMEOUT_MS} ms but the chain needs at least ` +
      `${required} ms (host ${HOST_STARTUP_BUDGET_MS} + app probe ${APP_PROBE_TIMEOUT_MS} ` +
      `+ margin ${MARGIN_MS}). Raising either app-side timeout requires raising this one.`,
  );
});

test("the timeout is a whole number of seconds so it survives a settings review", () => {
  assert.equal(NATIVE_TIMEOUT_MS % 1000, 0);
});

// ---------------------------------------------------------------------------
// hand-off decision

test("the host name and protocol version are the wire contract", () => {
  assert.equal(HOST_NAME, "com.potatodm.dm");
  assert.equal(PROTOCOL_VERSION, 1);
});

test("an explicit magnet or manifest kind wins over the URL", () => {
  assert.equal(handOffKind({ kind: "magnet", url: "https://cdn.test/a.mp4" }), "magnet");
  assert.equal(handOffKind({ kind: "manifest", url: "https://cdn.test/a.mp4" }), "manifest");
});

test("magnet and streaming manifest URLs are detected from the URL alone", () => {
  assert.equal(handOffKind({ url: "magnet:?xt=urn:btih:abc" }), "magnet");
  assert.equal(handOffKind({ url: "https://cdn.test/live/index.m3u8" }), "manifest");
  assert.equal(handOffKind({ url: "https://cdn.test/live/index.mpd" }), "manifest");
  assert.equal(handOffKind({ url: "https://cdn.test/live/index.m3u8?token=x" }), "manifest");
});

test("an ordinary file is not a special hand-off", () => {
  assert.equal(handOffKind({ url: "https://cdn.test/movie.mp4" }), undefined);
  assert.equal(handOffKind({}), undefined);
  assert.equal(handOffKind(null), undefined);
  assert.equal(handOffKind(undefined), undefined);
  // A URL that merely mentions m3u8 outside the extension is not a manifest.
  assert.equal(handOffKind({ url: "https://cdn.test/page?f=m3u8" }), undefined);
});

test("an unknown explicit kind is ignored rather than trusted", () => {
  assert.equal(handOffKind({ kind: "video", url: "https://cdn.test/movie.mp4" }), undefined);
  assert.equal(handOffKind({ kind: "video", url: "https://cdn.test/a.m3u8" }), "manifest");
});
