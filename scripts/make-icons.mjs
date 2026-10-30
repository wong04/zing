// Usage: node scripts/make-icons.mjs <svg-path> [out-dir]
// Rasterises the SVG with Electron (offscreen, transparent) into build/icons/.
import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, "..");
const PNG_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256, 512, 1024];
const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256];
// Windows enforces a minimum window size, so draw in a big window and crop.
const CANVAS = 1100;
const TILES = { "tile-150.png": 150, "tile-44.png": 44 };

export function buildIco(entries) {
	const header = Buffer.alloc(6);
	header.writeUInt16LE(0, 0);
	header.writeUInt16LE(1, 2);
	header.writeUInt16LE(entries.length, 4);
	const dir = Buffer.alloc(16 * entries.length);
	let offset = 6 + dir.length;
	entries.forEach(({ size, png }, i) => {
		const o = i * 16;
		dir.writeUInt8(size >= 256 ? 0 : size, o);
		dir.writeUInt8(size >= 256 ? 0 : size, o + 1);
		dir.writeUInt8(0, o + 2);
		dir.writeUInt8(0, o + 3);
		dir.writeUInt16LE(1, o + 4);
		dir.writeUInt16LE(32, o + 6);
		dir.writeUInt32LE(png.length, o + 8);
		dir.writeUInt32LE(offset, o + 12);
		offset += png.length;
	});
	return Buffer.concat([header, dir, ...entries.map((e) => e.png)]);
}

async function createWindow(BrowserWindow) {
	const win = new BrowserWindow({
		show: false,
		width: CANVAS,
		height: CANVAS,
		useContentSize: true,
		transparent: true,
		frame: false,
		webPreferences: { offscreen: true, backgroundThrottling: false },
	});
	win.webContents.setFrameRate(5);
	await win.loadURL("data:text/html;charset=utf-8," + encodeURIComponent("<style>html,body{margin:0;background:transparent;overflow:hidden}img{display:block;position:absolute;left:0;top:0}</style>"));
	return win;
}

async function rasterise(win, svgUrl, size) {
	const ok = await win.webContents.executeJavaScript(
		`new Promise(r=>{document.querySelectorAll('img').forEach(e=>e.remove());const i=new Image();i.style.width=i.style.height='${size}px';i.onload=()=>r(true);i.onerror=()=>r(false);i.src=${JSON.stringify(svgUrl)};document.body.append(i)})`,
	);
	if (!ok) throw new Error(`SVG failed to load at ${size}px`);
	// let the offscreen compositor produce a frame after decode
	await new Promise((r) => setTimeout(r, 300));
	const image = await win.webContents.capturePage({ x: 0, y: 0, width: size, height: size });
	const s = image.getSize();
	if (s.width !== size || s.height !== size) throw new Error(`captured ${s.width}x${s.height}, expected ${size}x${size}`);
	return image.toPNG();
}

async function runInElectron() {
	const { app, BrowserWindow } = await import("electron");
	app.commandLine.appendSwitch("force-device-scale-factor", "1");
	app.disableHardwareAcceleration();
	await app.whenReady();
	const [svgArg, outArg] = process.argv.slice(process.argv.indexOf("--") + 1);
	if (!svgArg) throw new Error("usage: node scripts/make-icons.mjs <svg-path> [out-dir]");
	const svgPath = resolve(svgArg);
	const svg = readFileSync(svgPath);
	const svgUrl = "data:image/svg+xml;base64," + svg.toString("base64");
	const out = resolve(outArg ?? join(root, "build", "icons"));
	mkdirSync(out, { recursive: true });

	const win = await createWindow(BrowserWindow);
	const cache = new Map();
	const get = async (size) => {
		if (!cache.has(size)) cache.set(size, await rasterise(win, svgUrl, size));
		return cache.get(size);
	};
	for (const size of PNG_SIZES) writeFileSync(join(out, `${size}.png`), await get(size));
	for (const [name, size] of Object.entries(TILES)) writeFileSync(join(out, name), await get(size));
	const entries = [];
	for (const size of ICO_SIZES) entries.push({ size, png: await get(size) });
	writeFileSync(join(out, "icon.ico"), buildIco(entries));
	console.log(`wrote ${PNG_SIZES.length} PNGs, 2 tiles, icon.ico to ${out}`);
}

if (process.versions.electron) {
	runInElectron().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
} else {
	const electron = (await import("node:module")).createRequire(import.meta.url)("electron");
	const r = spawnSync(electron, [fileURLToPath(import.meta.url), "--", ...process.argv.slice(2)], { stdio: "inherit" });
	process.exit(r.status ?? 1);
}
