import { test } from "node:test";
import assert from "node:assert/strict";

import "../shared/resources.js";

const {
  RESOURCE_LIMIT,
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
} = globalThis.PotatoDMResources;

// ---------------------------------------------------------------------------
// Naming

test("leafName keeps the last path segment and strips characters Windows rejects", () => {
  assert.equal(leafName("C:\\Users\\me\\movie.mp4"), "movie.mp4");
  assert.equal(leafName("/var/tmp/a/b.mkv"), "b.mkv");
  assert.equal(leafName('bad<>:"|?*.mp4'), "bad.mp4");
  assert.equal(leafName("   padded.txt   "), "padded.txt");
  assert.equal(leafName(""), "");
  assert.equal(leafName(null), "");
  assert.equal(leafName(undefined), "");
  assert.equal(leafName("x".repeat(300)).length, 200);
});

test("urlLeafName decodes the path but ignores the query string", () => {
  assert.equal(urlLeafName("https://cdn.test/a/my%20movie.mp4?token=abc"), "my movie.mp4");
  assert.equal(urlLeafName("https://cdn.test/"), "");
  assert.equal(urlLeafName("not a url"), "");
});

test("safePath lower-cases the pathname and tolerates junk", () => {
  assert.equal(safePath("https://cdn.test/A/B.MP4?x=1"), "/a/b.mp4");
  assert.equal(safePath("garbage?x=1#y"), "garbage");
  assert.equal(safePath(undefined), "");
});

// ---------------------------------------------------------------------------
// Classification

test("segment URLs are recognised so they can be dropped from the list", () => {
  assert.equal(isSegmentUrl("https://cdn.test/hls/seg-12.ts"), true);
  assert.equal(isSegmentUrl("https://cdn.test/hls/chunk_3.m4s"), true);
  assert.equal(isSegmentUrl("https://cdn.test/hls/init.mp4"), true);
  assert.equal(isSegmentUrl("https://cdn.test/movie.mp4"), false);
  assert.equal(isSegmentUrl("https://cdn.test/index.m3u8"), false);
});

test("kindForResource prefers the manifest, then video, then audio", () => {
  assert.equal(kindForResource("https://cdn.test/a.m3u8", ""), "manifest");
  assert.equal(kindForResource("https://cdn.test/x", "application/vnd.apple.mpegurl"), "manifest");
  assert.equal(kindForResource("https://cdn.test/x", "application/dash+xml"), "manifest");
  assert.equal(kindForResource("https://cdn.test/a.mp4", ""), "video");
  assert.equal(kindForResource("https://cdn.test/x", "video/webm"), "video");
  assert.equal(kindForResource("https://cdn.test/a.m4a", ""), "audio");
  assert.equal(kindForResource("https://cdn.test/x", "audio/mpeg"), "audio");
  assert.equal(kindForResource("https://cdn.test/x", "application/octet-stream"), "binary");
  assert.equal(kindForResource("https://cdn.test/page.html", "text/html"), "other");
});

test("a segment request is not interesting unless it is itself a manifest", () => {
  assert.equal(isInterestingResource("https://cdn.test/hls/seg-1.ts", "video/mp2t", "xmlhttprequest"), false);
  assert.equal(isInterestingResource("https://cdn.test/hls/index.m3u8", "", "xmlhttprequest"), true);
});

test("isInterestingResource accepts media types, media extensions and media requests", () => {
  assert.equal(isInterestingResource("https://cdn.test/x", "video/mp4", "media"), true);
  assert.equal(isInterestingResource("https://cdn.test/movie.mkv", "", ""), true);
  assert.equal(isInterestingResource("https://cdn.test/x", "application/octet-stream", ""), true);
  assert.equal(isInterestingResource("https://cdn.test/x", "", "media"), true);
  assert.equal(isInterestingResource("https://cdn.test/page.html", "text/html", "main_frame"), false);
});

// ---------------------------------------------------------------------------
// Sanitising and merging

test("sanitizeEntry drops anything that is not an http(s) URL", () => {
  assert.equal(sanitizeEntry(null), null);
  assert.equal(sanitizeEntry("https://cdn.test/a.mp4"), null);
  assert.equal(sanitizeEntry({ url: "  " }), null);
  assert.equal(sanitizeEntry({ url: "magnet:?xt=urn:btih:abc" }), null);
  assert.equal(sanitizeEntry({ url: "javascript:alert(1)" }), null);
});

test("sanitizeEntry fills in the name, kind and defaults", () => {
  const item = sanitizeEntry({ url: "https://cdn.test/dir/movie.mp4", at: 5 });

  assert.equal(item.url, "https://cdn.test/dir/movie.mp4");
  assert.equal(item.name, "movie.mp4");
  assert.equal(item.kind, "video");
  assert.equal(item.size, 0);
  assert.equal(item.referer, "");
  assert.equal(item.source, "network");
  assert.equal(item.at, 5);
});

test("sanitizeEntry keeps a supplied kind and referer but drops a bad one", () => {
  const kept = sanitizeEntry({
    url: "https://cdn.test/a.bin",
    kind: "page",
    referer: "https://site.test/watch",
  });
  assert.equal(kept.kind, "page");
  assert.equal(kept.referer, "https://site.test/watch");

  const dropped = sanitizeEntry({ url: "https://cdn.test/a.bin", referer: "magnet:?x" });
  assert.equal(dropped.referer, "");
});

test("sanitizeEntry rejects a non-positive or non-finite size", () => {
  assert.equal(sanitizeEntry({ url: "https://cdn.test/a.bin", size: -1 }).size, 0);
  assert.equal(sanitizeEntry({ url: "https://cdn.test/a.bin", size: Number.NaN }).size, 0);
  assert.equal(sanitizeEntry({ url: "https://cdn.test/a.bin", size: 12 }).size, 12);
});

test("merging the same URL twice yields one entry, ranked by kind", () => {
  const merged = mergeResources(
    [{ url: "https://cdn.test/a", kind: "other", at: 100, size: 7 }],
    [{ url: "https://cdn.test/a", kind: "video", at: 50 }],
  );

  assert.equal(merged.length, 1);
  assert.equal(merged[0].kind, "video");
  // Metadata from either report survives: size came from the weaker entry.
  assert.equal(merged[0].size, 7);
});

test("equal ranks keep the newer report and still merge metadata", () => {
  const merged = mergeResources(
    [{ url: "https://cdn.test/a", kind: "video", at: 100, name: "old.mp4" }],
    [{ url: "https://cdn.test/a", kind: "video", at: 200, contentType: "video/mp4" }],
  );

  assert.equal(merged.length, 1);
  assert.equal(merged[0].at, 200);
  assert.equal(merged[0].contentType, "video/mp4");
  assert.equal(merged[0].name, "old.mp4");
});

test("results are ordered manifest > video > audio > binary > page > other", () => {
  const list = mergeResources(
    [
      { url: "https://cdn.test/1", kind: "other", at: 1 },
      { url: "https://cdn.test/2", kind: "page", at: 2 },
      { url: "https://cdn.test/3", kind: "binary", at: 3 },
      { url: "https://cdn.test/4", kind: "audio", at: 4 },
      { url: "https://cdn.test/5", kind: "video", at: 5 },
      { url: "https://cdn.test/6", kind: "manifest", at: 6 },
    ],
    [],
  );

  assert.deepEqual(
    list.map((item) => item.kind),
    ["manifest", "video", "audio", "binary", "page", "other"],
  );
});

test("equal ranks are ordered newest first", () => {
  const list = mergeResources(
    [
      { url: "https://cdn.test/old", kind: "video", at: 10 },
      { url: "https://cdn.test/new", kind: "video", at: 20 },
    ],
    [],
  );

  assert.deepEqual(list.map((item) => item.url), [
    "https://cdn.test/new",
    "https://cdn.test/old",
  ]);
});

test("compareResources is a total order over rank then recency", () => {
  const video = { url: "v", kind: "video", at: 1 };
  const audio = { url: "a", kind: "audio", at: 99 };
  const unknown = { url: "u", kind: "nonexistent", at: 100 };

  assert.ok(compareResources(video, audio) < 0);
  assert.ok(compareResources(audio, video) > 0);
  assert.ok(compareResources(video, video) === 0);
  // An unknown kind ranks as 0 rather than throwing.
  assert.ok(compareResources(audio, unknown) < 0);
});

test("the merged list is capped at RESOURCE_LIMIT", () => {
  const many = Array.from({ length: RESOURCE_LIMIT + 25 }, (_, index) => ({
    url: `https://cdn.test/${index}`,
    kind: "video",
    at: index,
  }));

  assert.equal(mergeResources(many, []).length, RESOURCE_LIMIT);
});

test("sanitizeStoredList drops garbage restored from storage", () => {
  assert.deepEqual(sanitizeStoredList(null), []);
  assert.deepEqual(sanitizeStoredList("nope"), []);
  assert.equal(sanitizeStoredList([{ url: "magnet:?x" }, { url: "https://cdn.test/a.mp4", at: 1 }]).length, 1);
});
