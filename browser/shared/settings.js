"use strict";

// Classic script on purpose: `browser/shared/` is the extension root, and the MV3
// service worker, the content scripts and the popup page are all classic
// scripts. None of them can `import`. A namespace on `globalThis` is the lowest
// common denominator, and it is also what lets `node --test` load this file
// without a bundler.
(() => {
  const DEFAULT_MIN_SIZE_MB = 2;

  /**
   * Resolves the "do not intercept downloads smaller than this" threshold.
   *
   * The unit is MiB, matching the popup's options and the `fileSize` comparison
   * in background.js. The reason this function exists at all: `value || 2` treats
   * a stored `0` as "no value" and silently restores the default, so a user who
   * chose 不限制 saw the threshold come back after every service-worker restart.
   *
   * Absent, blank, non-numeric, non-finite and negative input falls back to the
   * default; every finite non-negative number is preserved exactly, including 0.
   */
  function resolveMinSizeMb(value) {
    // Only a number or a numeric string is a real setting. Anything else (array,
    // object, boolean) is coerced by `Number()` into something plausible — most
    // dangerously `Number([])` is 0, which reads as "no threshold" and would
    // silently intercept every download.
    if (typeof value === "string") {
      const trimmed = value.trim();
      if (trimmed === "") {
        return DEFAULT_MIN_SIZE_MB;
      }

      value = Number(trimmed);
    }

    if (typeof value !== "number") {
      return DEFAULT_MIN_SIZE_MB;
    }

    if (!Number.isFinite(value) || value < 0) {
      return DEFAULT_MIN_SIZE_MB;
    }

    return value;
  }

  globalThis.PotatoDMSettings = Object.freeze({
    DEFAULT_MIN_SIZE_MB,
    resolveMinSizeMb,
  });
})();
