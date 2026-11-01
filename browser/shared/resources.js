"use strict";

// Pure resource-discovery logic, extracted verbatim from background.js so it can
// be unit-tested without a browser. Behaviour is unchanged: the functions below
// were moved, not rewritten.
//
// Loaded as a classic script by the MV3 service worker (`importScripts`) and by
// `node --test` (side-effect import). See settings.js for why not ESM.
(() => {
  const RESOURCE_LIMIT = 60;

  const MEDIA_PATH_RE =
    /\.(m3u8|mpd|mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts|m4a|mp3|aac|flac|ogg|oga|opus|wav)$/i;
  const SEGMENT_PATH_RE =
    /\.(ts|m4s|m4f|cmfv|cmfa|cmft)$|(^|\/)init\.mp4$|\/(?:seg|segment|chunk|frag|fragment)[-_]?\d+\./i;
  const RESOURCE_KINDS = new Set(["manifest", "video", "audio", "binary", "page", "other"]);
  const RESOURCE_RANK = { manifest: 5, video: 4, audio: 3, binary: 2, page: 1, other: 0 };

  function leafName(value) {
    if (!value) {
      return "";
    }

    const parts = String(value).split(/[\\/]/);
    const name = parts[parts.length - 1] || "";
    return name
      .replace(/[<>:"|?*\u0000-\u001f]/g, "")
      .trim()
      .slice(0, 200);
  }

  function urlLeafName(url) {
    try {
      const parsed = new URL(url);
      return leafName(decodeURIComponent(parsed.pathname));
    } catch (_) {
      return "";
    }
  }

  function safePath(url) {
    try {
      return new URL(url).pathname.toLowerCase();
    } catch (_) {
      return String(url || "").split(/[?#]/)[0].toLowerCase();
    }
  }

  function isSegmentUrl(url) {
    const path = safePath(url);
    return SEGMENT_PATH_RE.test(path);
  }

  function kindForResource(url, contentType, requestType) {
    const type = String(contentType || "").toLowerCase();
    const path = safePath(url);
    if (/mpegurl|dash\+xml|mpeg-dash/.test(type) || /\.(m3u8|mpd)$/.test(path)) return "manifest";
    if (/^video\//.test(type) || /\.(mp4|m4v|webm|mkv|mov|avi|flv|f4v|wmv|m2ts|mpg|mpeg)$/.test(path)) return "video";
    if (/^audio\//.test(type) || /\.(m4a|mp3|aac|flac|ogg|oga|opus|wav)$/.test(path)) return "audio";
    if (/octet-stream|force-download|application\/mp4/.test(type) || requestType === "media") return "binary";
    return "other";
  }

  function isInterestingResource(url, contentType, requestType) {
    const type = String(contentType || "").toLowerCase();
    const path = safePath(url);
    if (isSegmentUrl(url)) return /mpegurl|dash\+xml/.test(type) || /\.(m3u8|mpd)$/.test(path);
    if (/^(video|audio)\//.test(type)) return true;
    if (/(mpegurl|dash\+xml|octet-stream|application\/mp4|force-download)/.test(type)) return true;
    if (MEDIA_PATH_RE.test(path)) return true;
    return requestType === "media";
  }

  function sanitizeEntry(raw) {
    if (!raw || typeof raw !== "object") return null;

    const url = typeof raw.url === "string" ? raw.url.trim().slice(0, 4096) : "";
    if (!/^https?:/i.test(url)) return null;

    const contentType = typeof raw.contentType === "string" ? raw.contentType.slice(0, 200) : "";
    const kind = RESOURCE_KINDS.has(raw.kind)
      ? raw.kind
      : kindForResource(url, contentType, "");
    const referer = typeof raw.referer === "string" && /^https?:/i.test(raw.referer)
      ? raw.referer.slice(0, 4096)
      : "";

    return {
      url,
      name: typeof raw.name === "string" ? raw.name.slice(0, 200) : urlLeafName(url),
      contentType,
      kind,
      size: Number.isFinite(raw.size) && raw.size > 0 ? raw.size : 0,
      referer,
      source: typeof raw.source === "string" ? raw.source.slice(0, 20) : "network",
      at: Number.isFinite(raw.at) ? raw.at : Date.now(),
    };
  }

  function compareResources(left, right) {
    return (RESOURCE_RANK[right.kind] || 0) - (RESOURCE_RANK[left.kind] || 0) ||
      (right.at || 0) - (left.at || 0);
  }

  function mergeResources(primary, secondary) {
    const byUrl = new Map();

    for (const item of [...primary, ...secondary]) {
      const existing = byUrl.get(item.url);
      if (!existing) {
        byUrl.set(item.url, item);
        continue;
      }

      const winner =
        (RESOURCE_RANK[item.kind] || 0) > (RESOURCE_RANK[existing.kind] || 0) ||
        ((RESOURCE_RANK[item.kind] || 0) === (RESOURCE_RANK[existing.kind] || 0) &&
          (item.at || 0) >= (existing.at || 0))
          ? item
          : existing;
      const other = winner === item ? existing : item;

      // Keep metadata from either report: the page-side scripts know URLs
      // first, while webRequest knows content type and size.
      byUrl.set(item.url, {
        ...other,
        ...winner,
        contentType: winner.contentType || other.contentType || "",
        size: winner.size || other.size || 0,
        name: winner.name || other.name || "",
        referer: winner.referer || other.referer || "",
      });
    }

    return Array.from(byUrl.values()).sort(compareResources).slice(0, RESOURCE_LIMIT);
  }

  function sanitizeStoredList(list) {
    if (!Array.isArray(list)) return [];
    const result = [];
    for (const raw of list) {
      const item = sanitizeEntry(raw);
      if (item) result.push(item);
    }
    return mergeResources(result, []);
  }

  globalThis.PotatoDMResources = Object.freeze({
    RESOURCE_LIMIT,
    RESOURCE_KINDS,
    RESOURCE_RANK,
    MEDIA_PATH_RE,
    SEGMENT_PATH_RE,
    leafName,
    urlLeafName,
    safePath,
    isSegmentUrl,
    kindForResource,
    isInterestingResource,
    sanitizeEntry,
    compareResources,
    mergeResources,
    sanitizeStoredList,
  });
})();
