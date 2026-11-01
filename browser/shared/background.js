"use strict";

// Classic MV3 worker; pure helpers are also exercised by node:test.
importScripts("browser-api.js", "settings.js", "resources.js", "protocol.js");

const { api, call, callStorage } = globalThis.PotatoDMBrowser;

const {
  HOST_NAME,
  PROTOCOL_VERSION,
  NATIVE_TIMEOUT_MS,
  handOffKind,
} = globalThis.PotatoDMProtocol;

const {
  RESOURCE_KINDS,
  RESOURCE_LIMIT,
  leafName,
  urlLeafName,
  isSegmentUrl,
  kindForResource,
  isInterestingResource,
  sanitizeEntry,
  mergeResources,
  sanitizeStoredList,
} = globalThis.PotatoDMResources;

const { resolveMinSizeMb } = globalThis.PotatoDMSettings;

const BYPASS_TTL_MS = 60000;
const MAX_COOKIES = 512;
const MAX_COOKIE_BYTES = 32 * 1024;
const RESOURCE_STORAGE_KEY = "resourcesByTab";
const GLOBAL_RESOURCE_TTL_MS = 10 * 60 * 1000;

// ---------------------------------------------------------------------------
// State

let enabled = true;
let lastResult = null;
let disabledSites = new Set();
let minSizeMb = 2;
const bypass = new Map(); // url -> expiry timestamp
const ownDownloadIds = new Set();
const resourcesByTab = new Map();
// Requests made by workers / service workers have tabId -1. They are kept
// separately and attributed to a tab by initiator origin when listed.
let globalResources = [];
let resourcesReady = null;
let persistTimer = null;
const sessionStore =
  (api.storage && api.storage.session) || (api.storage && api.storage.local);

function pruneBypass() {
  const now = Date.now();
  for (const [url, expiresAt] of bypass) {
    if (expiresAt <= now) {
      bypass.delete(url);
    }
  }
}

function markBypass(url) {
  pruneBypass();
  bypass.set(url, Date.now() + BYPASS_TTL_MS);
}

function isBypassed(url) {
  pruneBypass();
  return bypass.has(url);
}

callStorage(api.storage.local, "get", ["enabled", "lastResult", "disabledSites", "minSizeMb"])
  .then((values) => {
    if (!values) {
      return;
    }

    if (typeof values.enabled === "boolean") {
      enabled = values.enabled;
    }

    if (values.lastResult) {
      lastResult = values.lastResult;
    }
    disabledSites = new Set(Array.isArray(values.disabledSites) ? values.disabledSites : []);
    minSizeMb = resolveMinSizeMb(values.minSizeMb);
  })
  .catch(() => {});

api.storage.onChanged.addListener((changes, area) => {
  if (area !== "local") {
    return;
  }

  if (changes.enabled) {
    enabled = changes.enabled.newValue !== false;
  }

  if (changes.lastResult && changes.lastResult.newValue) {
    lastResult = changes.lastResult.newValue;
  }
  if (changes.disabledSites) disabledSites = new Set(changes.disabledSites.newValue || []);
  if (changes.minSizeMb) minSizeMb = resolveMinSizeMb(changes.minSizeMb.newValue);
});

// ---------------------------------------------------------------------------
// Helpers

function collectHeaders() {
  const headers = {};
  const languages =
    typeof navigator !== "undefined" && Array.isArray(navigator.languages)
      ? navigator.languages
      : [];

  if (languages.length > 0) {
    headers["Accept-Language"] = languages.slice(0, 8).join(", ");
  }

  headers["Upgrade-Insecure-Requests"] = "1";
  return headers;
}

async function collectCookies(url) {
  let cookies;

  try {
    cookies = await call(api.cookies.getAll, { url });
  } catch (_) {
    return null;
  }

  if (!Array.isArray(cookies) || cookies.length === 0) {
    return null;
  }

  cookies.sort(
    (left, right) => (right.path || "/").length - (left.path || "/").length,
  );

  const result = [];
  let bytes = 0;

  for (const cookie of cookies) {
    if (
      !cookie ||
      typeof cookie.name !== "string" ||
      typeof cookie.value !== "string" ||
      cookie.name.length === 0
    ) {
      continue;
    }

    bytes += cookie.name.length + cookie.value.length;
    if (bytes > MAX_COOKIE_BYTES || result.length >= MAX_COOKIES) {
      break;
    }

    result.push({
      name: cookie.name,
      value: cookie.value,
      domain: cookie.domain || "",
      path: cookie.path || "/",
      secure: cookie.secure === true,
      httpOnly: cookie.httpOnly === true,
      hostOnly: cookie.hostOnly === true,
      session:
        cookie.session === true || typeof cookie.expirationDate !== "number",
      expirationDate:
        typeof cookie.expirationDate === "number"
          ? cookie.expirationDate
          : null,
    });
  }

  return result.length > 0 ? result : null;
}

function recordResult(ok, detail) {
  lastResult = { ok, detail: String(detail || ""), at: Date.now() };
  callStorage(api.storage.local, "set", { lastResult }).catch(() => {});
}

function badge(text, color) {
  if (!api.action) {
    return;
  }

  call(api.action.setBadgeBackgroundColor, { color }).catch(() => {});
  call(api.action.setBadgeText, { text }).catch(() => {});

  setTimeout(() => {
    call(api.action.setBadgeText, { text: "" }).catch(() => {});
  }, 3000);
}

// ---------------------------------------------------------------------------
// Native messaging

function sendNative(request) {
  return new Promise((resolve) => {
    let settled = false;
    let port = null;

    const finish = (response, error) => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timer);

      if (port) {
        try {
          port.disconnect();
        } catch (_) {
          // Already disconnected.
        }
      }

      if (response && response.ok === true) {
        resolve(response);
      } else if (response) {
        resolve({
          ok: false,
          error: response.error || response.message || "host_error",
        });
      } else {
        resolve({ ok: false, error: error || "host_error" });
      }
    };

    const timer = setTimeout(() => finish(null, "timeout"), NATIVE_TIMEOUT_MS);

    try {
      port = api.runtime.connectNative(HOST_NAME);
    } catch (error) {
      finish(null, (error && error.message) || "host_error");
      return;
    }

    port.onMessage.addListener((response) => finish(response));
    port.onDisconnect.addListener(() => {
      // Reading lastError suppresses "Unchecked runtime.lastError".
      const error = api.runtime.lastError;
      finish(null, (error && error.message) || "host_unavailable");
    });

    try {
      port.postMessage(request);
    } catch (error) {
      finish(null, (error && error.message) || "host_error");
    }
  });
}

async function pingHost() {
  const response = await sendNative({
    version: PROTOCOL_VERSION,
    type: "ping",
  });

  return response.ok
    ? { ok: true }
    : { ok: false, error: response.error || "host_unavailable" };
}

// ---------------------------------------------------------------------------
// Download interception

function fallbackToBrowser(item) {
  markBypass(item.url);

  try {
    call(api.downloads.download, {
      url: item.url,
      conflictAction: "uniquify",
      saveAs: false,
    })
      .then((id) => {
        if (typeof id === "number") {
          ownDownloadIds.add(id);
        }
      })
      .catch(() => {});
  } catch (_) {
    // Nothing else to try; the browser's own cancellation stands.
  }
}

function removeBrowserEntry(id) {
  setTimeout(() => {
    call(api.downloads.removeFile, id)
      .catch(() => {})
      .then(() => call(api.downloads.erase, { id }).catch(() => {}));
  }, 250);
}

async function handOff(item) {
  let requestId;

  try {
    requestId =
      typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
        ? crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  } catch (_) {
    requestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }

  const request = {
    version: PROTOCOL_VERSION,
    type: "download",
    requestId,
    url: item.url,
    kind: handOffKind(item),
    contentType:
      typeof item.contentType === "string" ? item.contentType : undefined,
    suggestedFileName: leafName(item.filename) || urlLeafName(item.url),
    // The wire field is "referer" to match BrowserRequest.Referer.
    referer:
      typeof item.referrer === "string" ? item.referrer : "",
    userAgent:
      typeof navigator !== "undefined" && navigator.userAgent
        ? navigator.userAgent
        : "",
    cookies: await collectCookies(item.url),
    extraHeaders: collectHeaders(),
  };

  const response = await sendNative(request);

  if (response.ok && response.accepted === true) {
    badge("✓", "#1E8E3E");
    recordResult(true, response.targetPath || "");
    return true;
  }

  const reason = response.error || "declined";
  badge("↩", "#B06000");
  recordResult(false, reason);
  fallbackToBrowser(item);
  return false;
}

function pageOrigin(value) {
  try { return new URL(value).origin; } catch (_) { return ""; }
}

function isSiteEnabled(item) {
  const origin = pageOrigin(item.referrer || item.url);
  return !origin || !disabledSites.has(origin);
}

function onDownloadCreated(item) {
  if (!enabled || !item || typeof item.url !== "string") {
    return;
  }

  if (item.byExtensionId && item.byExtensionId === api.runtime.id) {
    return;
  }

  if (ownDownloadIds.has(item.id) || isBypassed(item.url)) {
    return;
  }

  if (!/^https?:/i.test(item.url)) {
    return;
  }

  if (!isSiteEnabled(item)) return;
  if (typeof item.fileSize === "number" && item.fileSize >= 0 && item.fileSize < minSizeMb * 1024 * 1024) return;

  // Cancel synchronously inside onCreated so the browser stops downloading.
  try {
    call(api.downloads.cancel, item.id).catch(() => {});
  } catch (_) {
    // The item may already have finished; hand-off still decides.
  }

  removeBrowserEntry(item.id);
  void handOff(item);
}

api.downloads.onCreated.addListener(onDownloadCreated);

// ---------------------------------------------------------------------------
// Resource discovery
//
// Metadata only: the network path never reads response bodies. The content
// scripts additionally report <video>/<a> URLs, performance entries and media
// URLs found in page data (see content.js / content-main.js).

function headerValue(headers, name) {
  const found = (headers || []).find((h) => h && String(h.name).toLowerCase() === name);
  return found && found.value ? found.value : "";
}

function pruneGlobalResources() {
  const cutoff = Date.now() - GLOBAL_RESOURCE_TTL_MS;
  globalResources = globalResources.filter((item) => item.at >= cutoff && item.referer);
}

function ensureResources() {
  if (resourcesReady) return resourcesReady;

  resourcesReady = (async () => {
    try {
      const values = await callStorage(sessionStore, "get", RESOURCE_STORAGE_KEY);
      const stored = values && values[RESOURCE_STORAGE_KEY];
      if (stored && typeof stored === "object") {
        const tabs = stored.tabs && typeof stored.tabs === "object" ? stored.tabs : {};
        for (const key of Object.keys(tabs)) {
          const tabId = Number(key);
          if (!Number.isInteger(tabId) || tabId < 0) continue;
          const list = sanitizeStoredList(tabs[key]);
          if (list.length > 0) {
            resourcesByTab.set(tabId, mergeResources(list, resourcesByTab.get(tabId) || []));
          }
        }
        globalResources = mergeResources(
          sanitizeStoredList(stored.global),
          globalResources,
        );
      }
    } catch (_) {
      // Session storage may be unavailable; in-memory discovery still works.
    }

    // Tab ids are reused after a browser restart. Drop entries for tabs that
    // no longer exist so a recycled id cannot show another page's resources.
    try {
      const tabs = await call(api.tabs.query, {});
      const live = new Set(
        (tabs || []).map((tab) => tab.id).filter((id) => typeof id === "number"),
      );
      for (const tabId of Array.from(resourcesByTab.keys())) {
        if (!live.has(tabId)) resourcesByTab.delete(tabId);
      }
    } catch (_) {
      // Keep the restored entries if tabs cannot be queried.
    }

    pruneGlobalResources();
  })();

  return resourcesReady;
}

function persistResources() {
  if (!sessionStore) return;

  const tabs = {};
  for (const [tabId, list] of resourcesByTab) {
    if (list && list.length > 0) tabs[String(tabId)] = list.slice(0, RESOURCE_LIMIT);
  }

  const value = {
    tabs,
    global: globalResources.slice(0, RESOURCE_LIMIT),
  };

  callStorage(sessionStore, "set", { [RESOURCE_STORAGE_KEY]: value }).catch(() => {});
}

function schedulePersist() {
  if (persistTimer) return;
  persistTimer = setTimeout(() => {
    persistTimer = null;
    persistResources();
  }, 250);
}

function addResource(tabId, raw) {
  const item = sanitizeEntry(raw);
  if (!item) return false;
  if (isSegmentUrl(item.url) && item.kind !== "manifest") return false;

  if (tabId < 0) {
    if (!item.referer) return false;
    globalResources = mergeResources([item], globalResources);
    pruneGlobalResources();
  } else {
    resourcesByTab.set(
      tabId,
      mergeResources([item], resourcesByTab.get(tabId) || []),
    );
  }

  schedulePersist();
  return true;
}

function clearTabResources(tabId) {
  if (resourcesByTab.delete(tabId)) schedulePersist();
}

function discoverResource(details) {
  if (!details || typeof details.url !== "string" || !/^https?:/i.test(details.url)) return;

  const contentType = headerValue(details.responseHeaders, "content-type");
  if (!isInterestingResource(details.url, contentType, details.type)) return;

  const tabId = typeof details.tabId === "number" ? details.tabId : -1;
  addResource(tabId, {
    url: details.url,
    name: urlLeafName(details.url),
    contentType,
    kind: kindForResource(details.url, contentType, details.type),
    size: Number(headerValue(details.responseHeaders, "content-length")) || 0,
    referer: typeof details.initiator === "string" ? details.initiator : "",
    source: "network",
    at: Date.now(),
  });
}

function acceptPageResources(sender, message) {
  const tabId = sender && sender.tab && typeof sender.tab.id === "number"
    ? sender.tab.id
    : -1;
  if (tabId < 0) return;

  const pageUrl = sender.tab && typeof sender.tab.url === "string" ? sender.tab.url : "";
  const items = Array.isArray(message.items) ? message.items.slice(0, 60) : [];

  for (const raw of items) {
    if (!raw || typeof raw.url !== "string") continue;
    addResource(tabId, {
      url: raw.url,
      name: typeof raw.name === "string" ? raw.name : "",
      contentType: typeof raw.contentType === "string" ? raw.contentType : "",
      kind: RESOURCE_KINDS.has(raw.kind) ? raw.kind : undefined,
      referer: pageUrl || (typeof raw.referer === "string" ? raw.referer : ""),
      source: typeof raw.source === "string" ? raw.source : "page",
      at: Date.now(),
    });
  }
}

async function listResourcesForTab(tabId) {
  await ensureResources();
  pruneGlobalResources();

  const list = (resourcesByTab.get(tabId) || []).slice();
  let tabUrl = "";

  try {
    const tab = await call(api.tabs.get, tabId);
    tabUrl = tab && typeof tab.url === "string" ? tab.url : "";
  } catch (_) {
    // The tab may have closed while the popup was open.
  }

  const origin = pageOrigin(tabUrl);
  if (origin) {
    for (const item of globalResources) {
      if (pageOrigin(item.referer) === origin) list.push(item);
    }
  }

  return mergeResources(list, []);
}

async function requestPageScan(tabId) {
  if (typeof tabId !== "number") return;

  try {
    await call(api.tabs.sendMessage, tabId, { type: "potato:scan" });
  } catch (_) {
    // No content script (restricted page or extension just installed).
  }

  // Content scripts report asynchronously; give them a moment to answer.
  await new Promise((resolve) => setTimeout(resolve, 400));
}

if (api.webRequest && api.webRequest.onHeadersReceived) {
  api.webRequest.onHeadersReceived.addListener(discoverResource, { urls: ["http://*/*", "https://*/*"] }, ["responseHeaders"]);
}

if (api.tabs) {
  if (api.tabs.onRemoved) {
    api.tabs.onRemoved.addListener((tabId) => clearTabResources(tabId));
  }

  if (api.tabs.onUpdated) {
    api.tabs.onUpdated.addListener((tabId, changeInfo) => {
      // Only a real navigation starts a new page. SPA history changes and
      // hash-only updates keep the list because the media is still the same.
      if (changeInfo && changeInfo.status === "loading") clearTabResources(tabId);
    });
  }
}

void ensureResources();

if (api.runtime.onInstalled && api.contextMenus) {
  api.runtime.onInstalled.addListener(() => {
    call(api.contextMenus.removeAll).catch(() => {}).then(() => call(api.contextMenus.create, {
      id: "potato-download", title: "使用土豆下载器下载", contexts: ["link", "video", "audio"]
    })).catch(() => {});
  });
  api.contextMenus.onClicked.addListener((info, tab) => {
    const url = info.linkUrl || info.srcUrl;
    if (url) void handOff({ url, filename: urlLeafName(url), referrer: tab && tab.url ? tab.url : "" });
  });
}

// ---------------------------------------------------------------------------
// Popup messaging

const POPUP_MESSAGE_TYPES = new Set([
  "potatodm:ping",
  "potato:list-resources",
  "potato:scan-page",
  "potato:download-resource",
  "potato:open-page",
]);

async function handlePopupMessage(message) {
  if (!message) return undefined;

  switch (message.type) {
    case "potatodm:ping":
      return pingHost();

    case "potato:list-resources":
      return {
        ok: true,
        resources: await listResourcesForTab(message.tabId),
      };

    case "potato:scan-page":
      await ensureResources();
      await requestPageScan(message.tabId);
      return {
        ok: true,
        resources: await listResourcesForTab(message.tabId),
      };

    case "potato:download-resource": {
      const resource = message.resource;
      if (!resource || typeof resource.url !== "string") return { ok: false };

      const accepted = await handOff({
        url: resource.url,
        kind: resource.kind,
        contentType: resource.contentType,
        filename: resource.name || urlLeafName(resource.url),
        referrer: resource.referer || "",
      });
      return { ok: accepted };
    }

    case "potato:open-page": {
      if (typeof message.url !== "string" || !/^https?:/i.test(message.url)) {
        return { ok: false };
      }

      try {
        await call(api.tabs.create, { url: message.url });
        return { ok: true };
      } catch (_) {
        return { ok: false };
      }
    }

    default:
      return undefined;
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message.type !== "string") return false;

  if (message.type === "potato:resources") {
    acceptPageResources(sender, message);
    return false;
  }

  if (!POPUP_MESSAGE_TYPES.has(message.type)) return false;

  handlePopupMessage(message, sender).then(sendResponse);
  return true;
});
