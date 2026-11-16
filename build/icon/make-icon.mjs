// Renders build/icon/icon.html at each icon size in headless Edge (DevTools protocol, Node built-ins only)
// and packs the PNGs into a multi-size .ico (PNG entries, Vista and later).
// Usage: node build/icon/make-icon.mjs [out.ico]   (default: src/GlassDock/GlassDock.ico)
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(process.argv[2] ?? path.join(here, "../../src/GlassDock/GlassDock.ico"));
const EDGE = "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe";
const PORT = 9399;
// Explorer and the taskbar ask for these at 100-200 % scaling.
const SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

const profile = fs.mkdtempSync(path.join(process.env.TEMP ?? here, "glassdock-icon-"));
const edge = spawn(EDGE, ["--headless=new", `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  "--hide-scrollbars", "--force-device-scale-factor=1", "--no-first-run", "about:blank"], { stdio: "ignore" });
edge.on("error", (e) => { console.error("Edge failed to start:", e.message); process.exit(1); });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let wsUrl;
for (let i = 0; i < 100 && !wsUrl; i++) {
  try { wsUrl = (await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json()).find((t) => t.type === "page")?.webSocketDebuggerUrl; }
  catch (e) { if (i === 99) throw e; } // still starting
  if (!wsUrl) await sleep(200);
}
const ws = new WebSocket(wsUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let id = 0;
const pending = new Map();
ws.onmessage = (m) => { const msg = JSON.parse(m.data); const p = pending.get(msg.id); if (!p) return; pending.delete(msg.id); msg.error ? p.rej(new Error(JSON.stringify(msg.error))) : p.res(msg.result); };
const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });

try {
  await send("Page.enable");
  await send("Emulation.setDefaultBackgroundColorOverride", { color: { r: 0, g: 0, b: 0, a: 0 } });
  const images = [];
  for (const s of SIZES) {
    await send("Emulation.setDeviceMetricsOverride", { width: s, height: s, deviceScaleFactor: 1, mobile: false });
    await send("Page.navigate", { url: `${pathToFileURL(path.join(here, "icon.html")).href}?s=${s}` });
    await sleep(400);
    const shot = await send("Page.captureScreenshot", { format: "png", clip: { x: 0, y: 0, width: s, height: s, scale: 1 } });
    images.push({ s, png: Buffer.from(shot.data, "base64") });
  }

  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ s, png }, k) => {
    const e = 6 + 16 * k;
    header.writeUInt8(s >= 256 ? 0 : s, e); header.writeUInt8(s >= 256 ? 0 : s, e + 1); // 0 means 256
    header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(png.length, e + 8); header.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  fs.writeFileSync(out, Buffer.concat([header, ...images.map((i) => i.png)]));
  console.log(`${out}: ${images.map((i) => i.s).join(", ")} px, ${(offset / 1024).toFixed(0)} KB`);
  if (process.env.ICON_PREVIEW_DIR) {
    for (const { s, png } of images) fs.writeFileSync(path.join(process.env.ICON_PREVIEW_DIR, `icon-${s}.png`), png);
    console.log(`previews in ${process.env.ICON_PREVIEW_DIR}`);
  }
} finally {
  ws.close();
  edge.kill();
}
