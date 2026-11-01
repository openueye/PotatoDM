import { test } from "node:test";
import assert from "node:assert/strict";

import "../shared/settings.js";

const { DEFAULT_MIN_SIZE_MB, resolveMinSizeMb } = globalThis.PotatoDMSettings;

// The whole point of resolveMinSizeMb: a stored 0 means "不限制" and must survive
// a service-worker restart. `value || 2` treated it as "absent" and put the
// default back, so the threshold silently reappeared.
test("a stored 0 is preserved instead of falling back to the default", () => {
  assert.equal(resolveMinSizeMb(0), 0);
  assert.equal(resolveMinSizeMb("0"), 0);
  assert.equal(resolveMinSizeMb(" 0 "), 0);
});

test("absent or unusable values fall back to the default", () => {
  const unusable = [
    undefined,
    null,
    "",
    "   ",
    "abc",
    Number.NaN,
    Number.POSITIVE_INFINITY,
    Number.NEGATIVE_INFINITY,
    -1,
    "-1",
    {},
    [],
    [5],
    true,
    false,
  ];

  for (const value of unusable) {
    assert.equal(
      resolveMinSizeMb(value),
      DEFAULT_MIN_SIZE_MB,
      `expected the default for ${JSON.stringify(value)}`,
    );
  }
});

test("every real threshold round-trips", () => {
  // Every value the popup offers, as a number and as the string it writes.
  for (const value of [0, 1, 2, 5, 10, 32]) {
    assert.equal(resolveMinSizeMb(value), value);
    assert.equal(resolveMinSizeMb(String(value)), value);
  }

  assert.equal(resolveMinSizeMb(2.5), 2.5);
});

test("the default is the same number the old code used", () => {
  assert.equal(DEFAULT_MIN_SIZE_MB, 2);
});
