"use strict";

// Chrome and Edge share the callback API, including runtime.lastError.
(() => {
  function call(fn, ...args) {
    return new Promise((resolve, reject) => {
      fn.apply(chrome, [...args, (result) => {
        const error = chrome.runtime.lastError;
        if (error) reject(new Error(error.message));
        else resolve(result);
      }]);
    });
  }

  // Storage methods require their own area as the receiver.
  function callStorage(area, method, ...args) {
    return call(area[method].bind(area), ...args);
  }

  globalThis.PotatoDMBrowser = { api: chrome, call, callStorage };
})();
