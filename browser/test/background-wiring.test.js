import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { createContext, runInContext } from "node:vm";

// background.js is a classic MV3 service worker: it has no exports and cannot be
// imported. Running its real source in a sandbox with a stubbed `chrome` is the
// only way to prove the `importScripts` + destructuring wiring actually resolves
// and that the download-intercept path still behaves.

const SHARED = new URL("../shared/", import.meta.url);

function readShared(name) {
  return readFileSync(new URL(name, SHARED), "utf8");
}

function buildEnvironment(storedSettings) {
  const recorded = { cancelled: [], downloaded: [], listeners: {} };

  const noop = () => {};
  const area = (store) => ({
    get(keys, callback) {
      const result = {};
      for (const key of Array.isArray(keys) ? keys : [keys]) {
        if (key in store) {
          result[key] = store[key];
        }
      }
      callback(result);
    },
    set(items, callback) {
      Object.assign(store, items);
      callback();
    },
  });

  const chrome = {
    runtime: {
      id: "test-extension",
      lastError: null,
      onMessage: { addListener: noop },
      onInstalled: { addListener: noop },
      // No native host in a unit test: the hand-off must degrade, not throw.
      connectNative() {
        throw new Error("no native host");
      },
    },
    storage: {
      local: area({ ...storedSettings }),
      session: area({}),
      onChanged: { addListener: noop },
    },
    downloads: {
      onCreated: {
        addListener(listener) {
          recorded.listeners.created = listener;
        },
      },
      cancel(id, callback) {
        recorded.cancelled.push(id);
        callback();
      },
      download(options, callback) {
        recorded.downloaded.push(options.url);
        callback(1);
      },
      removeFile(id, callback) {
        callback();
      },
      erase(details, callback) {
        callback();
      },
    },
    action: {
      setBadgeBackgroundColor(options, callback) {
        callback();
      },
      setBadgeText(options, callback) {
        callback();
      },
    },
    tabs: {
      query(query, callback) {
        callback([]);
      },
      get(id, callback) {
        callback(null);
      },
      sendMessage(id, message, callback) {
        callback(null);
      },
      onRemoved: { addListener: noop },
      onUpdated: { addListener: noop },
    },
    webRequest: { onHeadersReceived: { addListener: noop } },
    contextMenus: {
      removeAll(callback) {
        callback();
      },
      create(options, callback) {
        callback();
      },
      onClicked: { addListener: noop },
    },
    cookies: {
      getAll(options, callback) {
        callback([]);
      },
    },
  };

  const sandbox = {
    chrome,
    console,
    // Real timers, unref'd: badge/cleanup timers must not keep the runner alive.
    setTimeout(handler, delay) {
      const timer = setTimeout(handler, delay);
      if (typeof timer.unref === "function") {
        timer.unref();
      }
      return timer;
    },
    clearTimeout(handler) {
      clearTimeout(handler);
    },
  };

  const context = createContext(sandbox);

  // `importScripts` executes the sibling classic scripts in the same global, which
  // is exactly what the browser does.
  sandbox.importScripts = (...files) => {
    for (const file of files) {
      runInContext(readShared(file), context, { filename: file });
    }
  };

  runInContext(readShared("background.js"), context, { filename: "background.js" });

  return { recorded, context };
}

/** The storage load resolves through a promise chain; let it settle. */
async function settle() {
  for (let index = 0; index < 5; index++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

const SMALL_DOWNLOAD = {
  id: 1,
  url: "https://example.test/small.bin",
  fileSize: 100 * 1024,
  byExtensionId: undefined,
};

test("background.js loads and registers the download listener", () => {
  const { recorded, context } = buildEnvironment({ enabled: true });

  assert.equal(typeof recorded.listeners.created, "function");
  // Shared scripts were really executed in the worker's global.
  assert.equal(typeof context.PotatoDMResources.mergeResources, "function");
  assert.equal(typeof context.PotatoDMSettings.resolveMinSizeMb, "function");
  assert.ok(context.PotatoDMProtocol.NATIVE_TIMEOUT_MS > 0);
});

test("every script the worker imports is shipped next to it", () => {
  const source = readShared("background.js");
  const call = /importScripts\(([^)]*)\)/.exec(source);

  assert.ok(call, "background.js no longer calls importScripts");

  const files = [...call[1].matchAll(/"([^"]+)"/g)].map((match) => match[1]);
  assert.deepEqual(files, ["settings.js", "resources.js", "protocol.js"]);

  for (const file of files) {
    assert.ok(existsSync(new URL(file, SHARED)), `${file} is missing from shared/`);
  }
});

test("popup.html loads every namespace popup.js destructures, before popup.js", () => {
  // popup.js destructures globalThis.PotatoDM… at load time, so a missing or
  // reordered script tag breaks the popup with a TypeError that only appears when
  // the user clicks the toolbar button. Rather than pinning a fixed script list,
  // resolve each namespace to the file that defines it and assert the order.
  const namespaces = [...readShared("popup.js").matchAll(/globalThis\.(PotatoDM\w+)/g)].map(
    (match) => match[1],
  );

  assert.ok(namespaces.length > 0, "popup.js no longer uses a shared namespace");

  const html = readFileSync(new URL("popup.html", SHARED), "utf8");
  const popupAt = html.indexOf('src="popup.js"');
  assert.ok(popupAt >= 0, "popup.html does not load popup.js");

  for (const namespace of namespaces) {
    const definer = readdirSync(SHARED).find((file) =>
      file.endsWith(".js") &&
      new RegExp(`globalThis\\.${namespace}\\s*=`).test(readShared(file)),
    );

    assert.ok(definer, `${namespace} is destructured by popup.js but no shared script defines it`);

    const at = html.indexOf(`src="${definer}"`);
    assert.ok(at >= 0, `popup.html does not load ${definer}, which defines ${namespace}`);
    assert.ok(at < popupAt, `${definer} must be loaded before popup.js`);
  }
});

test("a stored threshold of 0 survives the restart and keeps intercepting", async () => {
  // This is the G1-a regression. 0 means 不限制 — every download is handed off,
  // including tiny ones. The worker had just been suspended and resumed, so the
  // value came back from storage; `Number(...) || 2` turned 0 into 2 and the
  // threshold silently came back.
  const { recorded } = buildEnvironment({ enabled: true, minSizeMb: 0 });
  await settle();

  recorded.listeners.created({ ...SMALL_DOWNLOAD });

  assert.deepEqual(
    recorded.cancelled,
    [SMALL_DOWNLOAD.id],
    "a 100 KiB download was skipped even though the threshold was set to 0 (不限制)",
  );
});

test("a stored 0 also intercepts a download of a few bytes", async () => {
  const { recorded } = buildEnvironment({ enabled: true, minSizeMb: 0 });
  await settle();

  recorded.listeners.created({ ...SMALL_DOWNLOAD, id: 7, fileSize: 1 });

  assert.deepEqual(recorded.cancelled, [7]);
});

test("the default threshold still protects small downloads", async () => {
  // No stored value at all: the default (2 MiB) applies, so the same 100 KiB
  // download is left to the browser.
  const { recorded } = buildEnvironment({ enabled: true });
  await settle();

  recorded.listeners.created({ ...SMALL_DOWNLOAD });

  assert.deepEqual(recorded.cancelled, []);
});

test("a stored threshold of 2 also protects small downloads", async () => {
  const { recorded } = buildEnvironment({ enabled: true, minSizeMb: 2 });
  await settle();

  recorded.listeners.created({ ...SMALL_DOWNLOAD });

  assert.deepEqual(recorded.cancelled, []);
});

test("a download just above the threshold is intercepted", async () => {
  const { recorded } = buildEnvironment({ enabled: true, minSizeMb: 2 });
  await settle();

  recorded.listeners.created({
    ...SMALL_DOWNLOAD,
    fileSize: 2 * 1024 * 1024 + 1,
  });

  assert.deepEqual(recorded.cancelled, [SMALL_DOWNLOAD.id]);
});
