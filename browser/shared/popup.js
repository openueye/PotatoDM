"use strict";

// settings.js is loaded by popup.html before this file. It is a classic script, so
// the namespace is the only way to share the threshold resolver with the worker.
const { resolveMinSizeMb } = globalThis.PotatoDMSettings;

const { api, call, callStorage } = globalThis.PotatoDMBrowser;

function sendMessage(message) {
  return call(api.runtime.sendMessage.bind(api.runtime), message);
}

const REASON_TEXT = {
  no_length: "服务器未提供文件大小",
  probe_timeout: "探测超时",
  cancelled: "已取消",
  unsupported_encoding: "编码不受支持",
  http_error: "服务器拒绝",
  storage_unavailable: "保存目录不可用",
  app_unavailable: "客户端不可用",
  host_unavailable: "宿主不可用",
  host_error: "宿主异常",
  timeout: "宿主响应超时",
  declined: "客户端拒绝",
  invalid_request: "请求无效",
  internal_error: "内部错误",
};

const toggle = document.getElementById("enabled");
const toggleLabel = document.getElementById("enabled-label");
const statusLine = document.getElementById("status");
const lastLine = document.getElementById("last");
const hint = document.getElementById("hint");
const siteToggle = document.getElementById("site-enabled");
const minSize = document.getElementById("min-size");
const resources = document.getElementById("resources");
const refreshButton = document.getElementById("refresh-resources");
const magnetInput = document.getElementById("magnet");
const magnetButton = document.getElementById("magnet-send");
let activeTab = null;
let refreshing = false;
let pollTimer = null;

const KIND_LABELS = {
  manifest: "HLS/DASH 清单",
  video: "视频",
  audio: "音频",
  binary: "媒体文件",
  page: "视频页面",
  other: "资源",
};

function formatSize(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "";

  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${value >= 10 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

function reasonText(reason) {
  return REASON_TEXT[reason] || reason || "未知原因";
}

function renderEnabled(value) {
  toggle.checked = value;
  toggleLabel.textContent = value ? "已开启" : "已关闭";
}

function renderLast(result) {
  if (!result || typeof result.at !== "number") {
    lastLine.textContent = "还没有接管记录。";
    return;
  }

  const time = new Date(result.at).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });

  lastLine.textContent = result.ok
    ? `${time} 已交给土豆下载器`
    : `${time} 已回退浏览器（${reasonText(result.detail)}）`;
}

function setStatus(state, text) {
  statusLine.dataset.state = state;
  statusLine.textContent = text;
}

toggle.addEventListener("change", () => {
  renderEnabled(toggle.checked);
  callStorage(api.storage.local, "set", { enabled: toggle.checked }).catch(() => {});
});

siteToggle.addEventListener("change", async () => {
  if (!activeTab || !activeTab.url) return;
  const origin = new URL(activeTab.url).origin;
  const values = await callStorage(api.storage.local, "get", ["disabledSites"]);
  const sites = new Set((values && values.disabledSites) || []);
  if (siteToggle.checked) sites.delete(origin); else sites.add(origin);
  await callStorage(api.storage.local, "set", { disabledSites: Array.from(sites) });
});

minSize.addEventListener("change", () => {
  callStorage(api.storage.local, "set", { minSizeMb: resolveMinSizeMb(minSize.value) }).catch(() => {});
});

function renderResources(items) {
  resources.textContent = "";
  if (!items || items.length === 0) {
    resources.innerHTML =
      '<p class="empty">暂未发现媒体资源。<br>若这是视频列表页，请点开一个视频并开始播放后再查看；也可以点“重新扫描”。</p>';
    return;
  }

  for (const item of items) {
    const row = document.createElement("div");
    row.className = "resource";

    const name = document.createElement("span");
    name.className = "resource-name";
    name.textContent = item.name || item.url;
    name.title = item.url;

    const meta = document.createElement("div");
    meta.className = "resource-meta";
    const parts = [KIND_LABELS[item.kind] || "可下载资源"];
    if (item.contentType) parts.push(item.contentType.split(";")[0]);
    const size = formatSize(item.size);
    if (size) parts.push(size);
    meta.textContent = parts.join(" · ");

    const button = document.createElement("button");

    if (item.kind === "page") {
      meta.textContent = "视频页面 · 打开并播放后可嗅探到实际视频";
      button.textContent = "打开";
      button.addEventListener("click", async () => {
        button.disabled = true;
        try {
          const result = await sendMessage({ type: "potato:open-page", url: item.url });
          button.textContent = result && result.ok ? "已打开" : "失败";
        } catch (_) {
          button.textContent = "失败";
        }
      });
    } else {
      button.textContent = "下载";
      button.addEventListener("click", async () => {
        button.disabled = true;
        try {
          const result = await sendMessage({
            type: "potato:download-resource",
            resource: item,
          });
          button.textContent = result && result.ok ? "已发送" : "失败";
        } catch (_) {
          button.textContent = "失败";
        }
      });
    }

    row.append(name, meta, button);
    resources.appendChild(row);
  }
}

async function refreshResources(scan) {
  if (!activeTab || refreshing) return;
  refreshing = true;

  try {
    const found = await sendMessage({
      type: scan ? "potato:scan-page" : "potato:list-resources",
      tabId: activeTab.id,
    });
    renderResources(found && found.resources);
  } catch (_) {
    // The tab may have navigated or closed.
  } finally {
    refreshing = false;
  }
}

async function sendMagnet() {
  if (!magnetInput || !magnetButton) return;

  const url = magnetInput.value.trim();
  if (!/^magnet:\?/i.test(url)) {
    magnetInput.focus();
    return;
  }

  magnetButton.disabled = true;

  try {
    const result = await sendMessage({
      type: "potato:download-resource",
      resource: { url, kind: "magnet", name: "" },
    });
    magnetButton.textContent = result && result.ok ? "已发送" : "失败";
    if (result && result.ok) magnetInput.value = "";
  } catch (_) {
    magnetButton.textContent = "失败";
  }

  setTimeout(() => {
    magnetButton.disabled = false;
    magnetButton.textContent = "下载";
  }, 1500);
}

async function init() {
  try {
    const values = await callStorage(api.storage.local, "get", [
      "enabled",
      "lastResult",
      "disabledSites",
      "minSizeMb",
    ]);
    renderEnabled(!values || values.enabled !== false);
    renderLast(values ? values.lastResult : null);
    minSize.value = String(resolveMinSizeMb(values && values.minSizeMb));
    const tabs = await call(api.tabs.query, { active: true, currentWindow: true });
    activeTab = tabs && tabs[0];
    if (activeTab && activeTab.url) {
      const origin = new URL(activeTab.url).origin;
      siteToggle.checked = !((values && values.disabledSites) || []).includes(origin);
      await refreshResources(true);
      pollTimer = setInterval(() => {
        if (!document.hidden) void refreshResources(false);
      }, 2000);
    } else {
      renderResources([]);
    }
  } catch (_) {
    renderEnabled(true);
  }

  setStatus("checking", "正在检查客户端…");

  try {
    const response = await sendMessage({ type: "potatodm:ping" });

    if (response && response.ok) {
      setStatus("ready", "客户端已就绪");
      hint.hidden = true;
    } else if (response && response.error === "app_unavailable") {
      setStatus("down", response.message || "客户端未运行（开始下载时会自动启动）");
      hint.hidden = true;
    } else {
      setStatus("missing", "未检测到浏览器宿主");
      hint.hidden = false;
    }
  } catch (_) {
    setStatus("missing", "未检测到浏览器宿主");
    hint.hidden = false;
  }
}

if (refreshButton) {
  refreshButton.addEventListener("click", () => {
    void refreshResources(true);
  });
}

if (magnetButton) {
  magnetButton.addEventListener("click", () => {
    void sendMagnet();
  });
}

if (magnetInput) {
  magnetInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      void sendMagnet();
    }
  });
}

void init();
