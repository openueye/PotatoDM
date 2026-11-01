"use strict";

// The wire contract with the native host, plus the one decision that has to agree
// with it: which downloads are handed off at all.
(() => {
  const HOST_NAME = "com.potatodm.dm";
  const PROTOCOL_VERSION = 1;

  // How long the extension waits for the native host before it gives up and falls
  // back to the browser's own download. This timer is the LAST link in a chain,
  // so it has to be the longest one; if it fires first the extension re-downloads
  // a file the desktop app is still working on, and the user gets it twice.
  //
  //   host startup budget   15 s   src/PotatoDM.BrowserHost/Program.cs:16 DefaultBudget
  //   app-side probe         8 s   DownloadManager browserProbeTimeout
  //   margin                 2 s
  //   ---------------------------
  //   required            >= 25 s
  //
  // The relationship is asserted in test/protocol.test.js so that changing either
  // side without revisiting this number turns a test red.
  const NATIVE_TIMEOUT_MS = 30000;

  function handOffKind(item) {
    if (item && (item.kind === "magnet" || item.kind === "manifest")) {
      return item.kind;
    }

    const url = item && typeof item.url === "string" ? item.url : "";
    if (/^magnet:/i.test(url)) return "magnet";
    if (/\.(m3u8|mpd)(?:[?#]|$)/i.test(url)) return "manifest";
    return undefined;
  }

  globalThis.PotatoDMProtocol = Object.freeze({
    HOST_NAME,
    PROTOCOL_VERSION,
    NATIVE_TIMEOUT_MS,
    handOffKind,
  });
})();
