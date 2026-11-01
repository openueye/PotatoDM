"use strict";

// Page-side discovery. Runs in the extension's isolated world, so it can use
// chrome.* APIs while still reading the DOM and the page's performance
// timeline. content-main.js (MAIN world) reports media URLs found in page
// data and API payloads; this script relays those and scans the page itself.

(() => {
  const api = chrome;

  const MAX_PER_SCAN = 60;
  const MEDIA_PATH_RE =
    /\.(m3u8|mpd|mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts|m4a|mp3|aac|flac|ogg|oga|opus|wav)$/i;
  const SEGMENT_PATH_RE =
    /\.(ts|m4s|m4f|cmfv|cmfa|cmft)$|(^|\/)init\.mp4$|\/(?:seg|segment|chunk|frag|fragment)[-_]?\d+\./i;
  const PLAY_PAGE_RE =
    /\/(?:play|watch|video|vod|detail|view|movie|episode|watch-detail)\//i;

  const sent = new Set();
  let scanTimer = null;
  let lastScanAt = 0;

  function sendMessage(payload) {
    try {
      // Consume lastError when the worker is asleep or the page is shutting down.
      api.runtime.sendMessage(payload, () => {
        void api.runtime.lastError;
      });
    } catch (_) {
      // Extension context invalidated (reload/update); nothing to report to.
    }
  }

  function absoluteUrl(value) {
    if (typeof value !== "string") return "";

    const trimmed = value.trim();
    if (trimmed.length < 6 || trimmed.length > 4096) return "";
    if (/^(blob|data|javascript|magnet|about|chrome):/i.test(trimmed)) return "";

    try {
      const url = new URL(trimmed, location.href);
      if (url.protocol !== "http:" && url.protocol !== "https:") return "";
      url.hash = "";
      return url.href;
    } catch (_) {
      return "";
    }
  }

  function pathOf(url) {
    try {
      return new URL(url).pathname.toLowerCase();
    } catch (_) {
      return "";
    }
  }

  function isSegment(url) {
    return SEGMENT_PATH_RE.test(pathOf(url));
  }

  function isMediaPath(url) {
    return MEDIA_PATH_RE.test(pathOf(url));
  }

  function kindOf(url, initiatorType) {
    const path = pathOf(url);
    if (/\.(m3u8|mpd)$/.test(path)) return "manifest";
    if (/\.(mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts)$/.test(path)) return "video";
    if (/\.(m4a|mp3|aac|flac|ogg|oga|opus|wav)$/.test(path)) return "audio";
    if (initiatorType === "video" || initiatorType === "audio") return "video";
    return "other";
  }

  function leafName(url) {
    try {
      const parts = new URL(url).pathname.split("/");
      return decodeURIComponent(parts[parts.length - 1] || "").slice(0, 200);
    } catch (_) {
      return "";
    }
  }

  function pageTitle() {
    return (document.title || "").replace(/\s+/g, " ").trim().slice(0, 120);
  }

  function makeItem(url, name, initiatorType, source) {
    return {
      url,
      name: (name || "").slice(0, 200) || leafName(url) || pageTitle(),
      kind: kindOf(url, initiatorType),
      source,
    };
  }

  function collectMediaElements() {
    const items = [];
    const nodes = document.querySelectorAll("video, audio, source");

    for (const node of nodes) {
      if (items.length >= MAX_PER_SCAN) break;

      const candidates = [
        node.currentSrc,
        node.src,
        node.getAttribute("src"),
        node.getAttribute("data-src"),
      ];

      for (const candidate of candidates) {
        const url = absoluteUrl(candidate);
        if (!url || !isMediaPath(url) || isSegment(url)) continue;
        items.push(makeItem(url, "", node.tagName === "SOURCE" ? "video" : node.tagName.toLowerCase(), "player"));
        break;
      }
    }

    return items;
  }

  function collectMediaLinks() {
    const items = [];
    const anchors = document.querySelectorAll("a[href]");

    for (const anchor of anchors) {
      if (items.length >= MAX_PER_SCAN) break;

      const url = absoluteUrl(anchor.getAttribute("href"));
      if (!url || !isMediaPath(url) || isSegment(url)) continue;

      const text = (anchor.textContent || "").replace(/\s+/g, " ").trim();
      items.push(makeItem(url, text, "", "link"));
    }

    return items;
  }

  function collectPerformance() {
    const items = [];
    let entries;

    try {
      entries = performance.getEntriesByType("resource");
    } catch (_) {
      return items;
    }

    for (const entry of entries) {
      if (items.length >= MAX_PER_SCAN) break;

      const url = absoluteUrl(entry.name);
      if (!url || isSegment(url)) continue;

      const initiatorType = entry.initiatorType || "";
      const isMediaElement = initiatorType === "video" || initiatorType === "audio";
      if (!isMediaPath(url) && !isMediaElement) continue;

      items.push(makeItem(url, "", initiatorType, "network"));
    }

    return items;
  }

  function collectInlineScripts() {
    const items = [];
    const tokenPattern = /(?:https?:)?\/\/[^\s"'<>()\\]{4,2048}/gi;

    for (const script of document.querySelectorAll("script:not([src])")) {
      if (items.length >= MAX_PER_SCAN) break;

      const text = script.textContent || "";
      if (text.length === 0 || text.length > 2 * 1024 * 1024) continue;

      const tokens = text.match(tokenPattern) || [];
      for (const token of tokens) {
        if (items.length >= MAX_PER_SCAN) break;

        const url = absoluteUrl(token.replace(/[),;\]}]+$/, ""));
        if (!url || !isMediaPath(url) || isSegment(url)) continue;
        items.push(makeItem(url, "", "", "data"));
      }
    }

    return items;
  }

  // Listing pages often expose dozens of links such as /vod/play/123.html
  // while the streams themselves only load after opening and playing one.
  // Report repeated play/detail-looking links so the popup can offer them.
  function collectPlayPages() {
    const groups = new Map();
    const anchors = document.querySelectorAll("a[href]");
    const limit = Math.min(anchors.length, 3000);

    for (let index = 0; index < limit; index++) {
      const anchor = anchors[index];
      const url = absoluteUrl(anchor.getAttribute("href"));
      if (!url || url === location.href) continue;

      let parsed;
      try {
        parsed = new URL(url);
      } catch (_) {
        continue;
      }

      if (parsed.origin !== location.origin) continue;

      const path = parsed.pathname;
      if (isMediaPath(url) || isSegment(url)) continue;

      const looksLikePlayPage =
        PLAY_PAGE_RE.test(path) ||
        /[?&](?:vid|video|aid|id)=\d+/i.test(parsed.search);
      if (!looksLikePlayPage || !/\d/.test(path + parsed.search)) continue;

      const shape =
        path.replace(/\d+/g, "{n}") +
        "?" +
        parsed.search.replace(/\d+/g, "{n}");

      let group = groups.get(shape);
      if (!group) {
        group = { count: 0, urls: new Set(), items: [] };
        groups.set(shape, group);
      }

      if (group.urls.has(url)) continue;
      group.urls.add(url);
      group.count++;

      if (group.items.length < 12) {
        const text = (anchor.textContent || "").replace(/\s+/g, " ").trim();
        group.items.push({
          url,
          name: text.slice(0, 120) || leafName(url) || pageTitle(),
          kind: "page",
          source: "link",
        });
      }
    }

    const groups2 = Array.from(groups.values())
      .filter((group) => group.count >= 4)
      .sort((left, right) => right.count - left.count);

    const items = [];
    for (const group of groups2) {
      for (const item of group.items) {
        if (items.length >= 20) return items;
        items.push(item);
      }
    }

    return items;
  }

  function scan() {
    lastScanAt = Date.now();

    const batches = [
      collectMediaElements(),
      collectPerformance(),
      collectInlineScripts(),
      collectMediaLinks(),
      collectPlayPages(),
    ];

    const seen = new Set();
    const items = [];

    for (const batch of batches) {
      for (const item of batch) {
        if (!item || seen.has(item.url) || sent.has(item.url)) continue;
        seen.add(item.url);
        sent.add(item.url);
        items.push(item);
        if (items.length >= MAX_PER_SCAN) break;
      }
    }

    if (items.length > 0) {
      sendMessage({ type: "potato:resources", items });
    }

    // Ask the MAIN-world hook to hand over anything it collected before this
    // isolated script was ready (it runs at document_start, this one later).
    try {
      document.dispatchEvent(new Event("potatodm:flush"));
    } catch (_) {
      // Ignore; the main-world script also flushes on a timer.
    }
  }

  function scanSoon(delay) {
    if (scanTimer) return;
    scanTimer = setTimeout(() => {
      scanTimer = null;
      scan();
    }, delay || 700);
  }

  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data || event.data.type !== "potatodm:media") {
      return;
    }

    const urls = Array.isArray(event.data.urls) ? event.data.urls.slice(0, 40) : [];
    const items = [];

    for (const raw of urls) {
      const url = absoluteUrl(raw);
      if (!url || sent.has(url) || isSegment(url) || !isMediaPath(url)) continue;

      sent.add(url);
      items.push(makeItem(url, "", "video", "data"));
    }

    if (items.length > 0) {
      sendMessage({ type: "potato:resources", items });
    }
  });

  if (api.runtime.onMessage) {
    api.runtime.onMessage.addListener((message) => {
      if (!message || message.type !== "potato:scan") return undefined;
      scan();
      return undefined;
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => scanSoon(300), { once: true });
  } else {
    scanSoon(300);
  }

  window.addEventListener("load", () => scanSoon(400), { once: true });

  for (const type of ["play", "playing", "loadedmetadata"]) {
    document.addEventListener(type, () => scanSoon(300), true);
  }

  try {
    const observer = new MutationObserver((records) => {
      let anchorsSeen = 0;

      for (const record of records) {
        for (const node of record.addedNodes) {
          if (!node || node.nodeType !== 1) continue;

          if (
            node.tagName === "VIDEO" ||
            node.tagName === "AUDIO" ||
            node.tagName === "SOURCE"
          ) {
            scanSoon(500);
            return;
          }

          if (node.querySelector && node.querySelector("video, audio, source")) {
            scanSoon(500);
            return;
          }

          if (node.tagName === "A") {
            anchorsSeen++;
          } else if (node.querySelectorAll) {
            anchorsSeen += node.querySelectorAll("a[href]").length;
          }
        }
      }

      if (anchorsSeen >= 4) scanSoon(1200);
    });

    observer.observe(document.documentElement || document, {
      childList: true,
      subtree: true,
    });
  } catch (_) {
    // MutationObserver is optional.
  }

  try {
    const observer = new PerformanceObserver((list) => {
      const items = [];

      for (const entry of list.getEntries()) {
        const url = absoluteUrl(entry.name);
        if (!url || sent.has(url) || isSegment(url)) continue;

        const initiatorType = entry.initiatorType || "";
        const isMediaElement = initiatorType === "video" || initiatorType === "audio";
        if (!isMediaPath(url) && !isMediaElement) continue;

        sent.add(url);
        items.push(makeItem(url, "", initiatorType, "network"));
      }

      if (items.length > 0) {
        sendMessage({ type: "potato:resources", items });
      }
    });

    observer.observe({ entryTypes: ["resource"] });
  } catch (_) {
    // Older browsers may not support resource timing notifications.
  }

  // Catch resources loaded shortly after the initial scan (players often
  // bootstrap asynchronously) and refresh play-page links on SPA navigations.
  setTimeout(() => scanSoon(250), 2500);
  setTimeout(() => scanSoon(250), 7000);

  window.addEventListener("popstate", () => scanSoon(600));
  window.addEventListener("hashchange", () => scanSoon(600));
})();
