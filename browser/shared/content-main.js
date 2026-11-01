"use strict";

// MAIN-world companion to content.js. Runs at document_start so it can wrap
// JSON.parse / fetch / XMLHttpRequest before the page's own code does.
//
// Many sites never expose a media URL as a network request the extension can
// see: the player receives an encrypted API payload, decrypts it, and hands
// the resulting JSON to JSON.parse. Wrapping JSON.parse lets us inspect that
// decrypted data (and any other API payload) for media URLs without reading
// response bodies at the network layer.
//
// Everything found is posted to the isolated content script via window
// postMessage; the page can spoof such messages, so URLs are revalidated in
// content.js and in the background service worker.

(() => {
  const MEDIA_PATH_RE =
    /\.(m3u8|mpd|mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts|m4a|mp3|aac|flac|ogg|oga|opus|wav)(?:$|[?#])/i;
  const RELATIVE_MEDIA_RE =
    /^\/[^\s"'<>]{2,2048}\.(?:m3u8|mpd|mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts|m4a|mp3|aac|flac|ogg|oga|opus|wav)(?:\?[^\s"'<>]*)?$/i;
  const URL_TOKEN_RE = /(?:https?:)?\/\/[^\s"'<>()\\\u0000-\u001f]{4,2048}/gi;
  const MAX_URLS = 120;

  const found = new Set();
  const pending = new Set();
  let flushTimer = null;

  function normalize(raw) {
    if (typeof raw !== "string") return "";

    const value = raw.trim();
    if (value.length < 8 || value.length > 4096) return "";
    if (/^(?:blob|data|javascript|magnet):/i.test(value)) return "";
    if (!MEDIA_PATH_RE.test(value) && !RELATIVE_MEDIA_RE.test(value)) return "";

    try {
      const url = new URL(value, location.href);
      if (url.protocol !== "http:" && url.protocol !== "https:") return "";
      url.hash = "";
      return url.href;
    } catch (_) {
      return "";
    }
  }

  function remember(value) {
    if (found.size >= MAX_URLS) return;

    const url = normalize(value);
    if (!url || found.has(url)) return;

    found.add(url);
    pending.add(url);
    if (!flushTimer) flushTimer = setTimeout(flush, 250);
  }

  function scanString(value) {
    if (typeof value !== "string" || value.length === 0 || value.length > 8192) {
      return;
    }

    // The common case: the string is itself a media URL.
    if (normalize(value)) {
      remember(value);
      return;
    }

    if (value.indexOf("//") < 0 && value.indexOf("/") < 0) return;

    const tokens = value.match(URL_TOKEN_RE);
    if (!tokens) return;

    for (const token of tokens) {
      if (!MEDIA_PATH_RE.test(token)) continue;
      remember(token.replace(/[),;\]}]+$/, ""));
    }
  }

  function walk(value, depth, budget) {
    if (depth > 8 || budget.count > 4000) return;

    budget.count++;

    if (typeof value === "string") {
      scanString(value);
      return;
    }

    if (!value || typeof value !== "object") return;
    if (budget.seen.has(value)) return;
    budget.seen.add(value);

    if (Array.isArray(value)) {
      const length = Math.min(value.length, 1000);
      for (let index = 0; index < length; index++) {
        walk(value[index], depth + 1, budget);
        if (budget.count > 4000) return;
      }
      return;
    }

    let visited = 0;
    for (const key in value) {
      if (!Object.prototype.hasOwnProperty.call(value, key)) continue;
      walk(value[key], depth + 1, budget);
      if (++visited > 1000 || budget.count > 4000) return;
    }
  }

  function inspect(value) {
    if (value === null || value === undefined) return;

    try {
      walk(value, 0, { count: 0, seen: new WeakSet() });
    } catch (_) {
      // A proxy or exotic object can throw on property access.
    }
  }

  function flush() {
    if (flushTimer) {
      clearTimeout(flushTimer);
      flushTimer = null;
    }

    if (pending.size === 0) return;

    const urls = Array.from(pending).slice(0, 40);
    pending.clear();

    try {
      window.postMessage({ type: "potatodm:media", urls }, "*");
    } catch (_) {
      // Ignore.
    }
  }

  document.addEventListener("potatodm:flush", flush, true);

  // -------------------------------------------------------------------------
  // JSON.parse: covers jQuery dataType:"json", AES-decrypted API payloads, and
  // most player configuration objects.

  try {
    const originalParse = JSON.parse;

    if (typeof originalParse === "function") {
      JSON.parse = function parse(text, reviver) {
        const result = originalParse.call(this, text, reviver);
        inspect(result);
        return result;
      };
    }
  } catch (_) {
    // A frozen JSON object is unusual but not fatal.
  }

  // -------------------------------------------------------------------------
  // fetch(): inspect JSON/text responses the page reads itself.

  try {
    const originalFetch = window.fetch;

    if (typeof originalFetch === "function") {
      window.fetch = function fetch() {
        const promise = originalFetch.apply(this, arguments);

        promise.then(
          (response) => {
            try {
              const type = (response.headers.get("content-type") || "").toLowerCase();
              const length = Number(response.headers.get("content-length") || "0");
              if (length > 4 * 1024 * 1024) return;

              if (type.indexOf("json") >= 0) {
                response.clone().json().then(inspect).catch(() => {});
              } else if (
                type.indexOf("text/") === 0 ||
                type.indexOf("javascript") >= 0
              ) {
                response.clone().text().then(scanString).catch(() => {});
              }
            } catch (_) {
              // Opaque or already-consumed response.
            }
          },
          () => {},
        );

        return promise;
      };
    }
  } catch (_) {
    // Leave fetch untouched.
  }

  // -------------------------------------------------------------------------
  // XMLHttpRequest: covers libraries that do not go through JSON.parse.

  try {
    const originalOpen = XMLHttpRequest.prototype.open;

    XMLHttpRequest.prototype.open = function open() {
      try {
        this.addEventListener("load", () => {
          try {
            if (this.responseType === "json") {
              inspect(this.response);
              return;
            }

            if (this.responseType !== "" && this.responseType !== "text") return;

            const type = (this.getResponseHeader("content-type") || "").toLowerCase();
            if (
              type &&
              type.indexOf("json") < 0 &&
              type.indexOf("text/") < 0 &&
              type.indexOf("javascript") < 0
            ) {
              return;
            }

            const text = this.responseText;
            if (typeof text !== "string" || text.length > 4 * 1024 * 1024) return;

            if (type.indexOf("json") >= 0) {
              try {
                inspect(JSON.parse(text));
              } catch (_) {
                scanString(text);
              }
            } else {
              scanString(text);
            }
          } catch (_) {
            // Cross-origin or aborted request.
          }
        });
      } catch (_) {
        // Ignore listener failures; open still proceeds.
      }

      return originalOpen.apply(this, arguments);
    };
  } catch (_) {
    // Leave XMLHttpRequest untouched.
  }

  // Flush anything parsed before the isolated content script was ready.
  setTimeout(flush, 1200);
  setTimeout(flush, 5000);
})();
