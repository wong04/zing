// Usage: npm run make-icons
// Draws the logo (assets/brand/*.svg) with headless Edge, the engine WebView2 uses, into
// build/icons and build/icons-dark-tile, plus the Velopack installer art in build/installer.
// build/brand.json records which SVGs the files came from; the logo test fails when they drift.
/* global document, Image, ImageData -- used only inside page.evaluate, which runs in Edge */
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "@playwright/test";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
export const PNG_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256, 512, 1024];
export const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256];
const TILES = { "tile-150.png": 150, "tile-44.png": 44 };
export const SOURCES = { "build/icons": "assets/brand/zing.svg", "build/icons-dark-tile": "assets/brand/zing-dark-tile.svg" };
// The .exe, Setup.exe and the installer art use the frosted tile, which reads on both light and dark.
const INSTALLER_LOGO = "assets/brand/zing.svg";
// Velopack: --splashImage (any size), --msiTopBanner (493x58 BMP), --msiDialogBackground (493x312 BMP).
export const INSTALLER_ART = { "splash.png": [480, 300], "msi-top-banner.bmp": [493, 58], "msi-dialog-background.bmp": [493, 312] };

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

/** 24-bit bottom-up BMP from opaque RGBA pixels, the format MSI dialogs take. */
export function buildBmp(width, height, rgba) {
	const stride = Math.ceil((width * 3) / 4) * 4;
	const bmp = Buffer.alloc(54 + stride * height);
	bmp.write("BM", 0, "ascii");
	bmp.writeUInt32LE(bmp.length, 2);
	bmp.writeUInt32LE(54, 10);
	bmp.writeUInt32LE(40, 14);
	bmp.writeInt32LE(width, 18);
	bmp.writeInt32LE(height, 22);
	bmp.writeUInt16LE(1, 26);
	bmp.writeUInt16LE(24, 28);
	bmp.writeUInt32LE(stride * height, 34);
	for (let y = 0; y < height; y++) {
		const row = 54 + (height - 1 - y) * stride;
		for (let x = 0; x < width; x++) {
			const i = (y * width + x) * 4;
			if (rgba[i + 3] !== 255) throw new Error(`BMP art must be opaque; pixel ${x},${y} has alpha ${rgba[i + 3]}`);
			bmp[row + x * 3] = rgba[i + 2];
			bmp[row + x * 3 + 1] = rgba[i + 1];
			bmp[row + x * 3 + 2] = rgba[i];
		}
	}
	return bmp;
}

/** Hash with line endings normalised, so a CRLF checkout matches. */
export function svgHash(text) {
	return createHash("sha256").update(text.replace(/\r\n/g, "\n")).digest("hex");
}

// The SVG's own width/height are set to the target size, so Edge draws the vector at that size
// instead of scaling down a 1024 px bitmap.
function sizedSvg(svg, size) {
	const attrs = / width="\d+" height="\d+"/;
	if (!attrs.test(svg)) throw new Error("the logo SVG needs width and height attributes on its root");
	return svg.replace(attrs, ` width="${size}" height="${size}"`);
}

async function renderPng(page, svg, size) {
	const b64 = await page.evaluate(async ({ svg, size }) => {
		const img = new Image();
		img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
		await img.decode();
		const canvas = document.createElement("canvas");
		canvas.width = canvas.height = size;
		canvas.getContext("2d").drawImage(img, 0, 0, size, size);
		return canvas.toDataURL("image/png").split(",")[1];
	}, { svg: sizedSvg(svg, size), size });
	return Buffer.from(b64, "base64");
}

// Draws one piece of installer art in the page and returns its RGBA pixels.
async function renderArt(page, name, [width, height], logo) {
	const pixels = await page.evaluate(async ({ name, width, height, logo }) => {
		const load = async (size) => {
			const img = new Image();
			img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(logo.replace(/ width="\d+" height="\d+"/, ` width="${size}" height="${size}"`));
			await img.decode();
			return img;
		};
		await document.fonts.load('600 48px "Segoe UI Variable Display"');
		const canvas = document.createElement("canvas");
		canvas.width = width;
		canvas.height = height;
		const ctx = canvas.getContext("2d");
		const text = (s, x, y, px, weight, colour, align = "left") => {
			ctx.font = `${weight} ${px}px "Segoe UI Variable Display", "Segoe UI", sans-serif`;
			ctx.fillStyle = colour;
			ctx.textAlign = align;
			ctx.textBaseline = "alphabetic";
			ctx.fillText(s, x, y);
		};
		if (name === "splash.png") {
			const bg = ctx.createLinearGradient(0, 0, width, height);
			bg.addColorStop(0, "#FFFFFF");
			bg.addColorStop(1, "#E6E8F4");
			ctx.fillStyle = bg;
			ctx.fillRect(0, 0, width, height);
			ctx.drawImage(await load(176), 40, 62, 176, 176);
			text("Zing", 240, 158, 64, 600, "#1E2133");
			text("Installing…", 244, 192, 18, 400, "#5D6378");
		}
		if (name === "msi-top-banner.bmp") {
			ctx.fillStyle = "#FFFFFF";
			ctx.fillRect(0, 0, width, height);
			ctx.drawImage(await load(46), width - 46 - 10, 6, 46, 46);
		}
		if (name === "msi-dialog-background.bmp") {
			ctx.fillStyle = "#FFFFFF";
			ctx.fillRect(0, 0, width, height);
			const band = ctx.createLinearGradient(0, 0, 164, height);
			band.addColorStop(0, "#2C3150");
			band.addColorStop(1, "#14151F");
			ctx.fillStyle = band;
			ctx.fillRect(0, 0, 164, height);
			ctx.drawImage(await load(104), 30, 86, 104, 104);
			text("Zing", 82, 236, 30, 600, "#FFFFFF", "center");
		}
		return Array.from(ctx.getImageData(0, 0, width, height).data);
	}, { name, width, height, logo });
	return Uint8Array.from(pixels);
}

async function main() {
	const browser = await chromium.launch({ channel: "msedge" });
	try {
		const page = await browser.newPage();
		const manifest = {};
		for (const [outRel, srcRel] of Object.entries(SOURCES)) {
			const svg = readFileSync(join(root, srcRel), "utf8");
			manifest[srcRel] = svgHash(svg);
			const out = join(root, outRel);
			mkdirSync(out, { recursive: true });
			const cache = new Map();
			const get = async (size) => {
				if (!cache.has(size)) cache.set(size, await renderPng(page, svg, size));
				return cache.get(size);
			};
			for (const size of PNG_SIZES) writeFileSync(join(out, `${size}.png`), await get(size));
			for (const [name, size] of Object.entries(TILES)) writeFileSync(join(out, name), await get(size));
			const entries = [];
			for (const size of ICO_SIZES) entries.push({ size, png: await get(size) });
			writeFileSync(join(out, "icon.ico"), buildIco(entries));
			console.log(`${srcRel}: ${PNG_SIZES.length} PNGs, 2 tiles and icon.ico in ${outRel}`);
		}
		const logo = readFileSync(join(root, INSTALLER_LOGO), "utf8");
		const installer = join(root, "build", "installer");
		mkdirSync(installer, { recursive: true });
		for (const [name, dims] of Object.entries(INSTALLER_ART)) {
			const rgba = await renderArt(page, name, dims, logo);
			if (name.endsWith(".bmp")) {
				writeFileSync(join(installer, name), buildBmp(dims[0], dims[1], rgba));
				continue;
			}
			const b64 = await page.evaluate(({ width, height, pixels }) => {
				const canvas = document.createElement("canvas");
				canvas.width = width;
				canvas.height = height;
				canvas.getContext("2d").putImageData(new ImageData(Uint8ClampedArray.from(pixels), width, height), 0, 0);
				return canvas.toDataURL("image/png").split(",")[1];
			}, { width: dims[0], height: dims[1], pixels: Array.from(rgba) });
			writeFileSync(join(installer, name), Buffer.from(b64, "base64"));
		}
		console.log(`installer art: ${Object.keys(INSTALLER_ART).join(", ")} in build/installer`);
		writeFileSync(join(root, "build", "brand.json"), JSON.stringify(manifest, null, "\t") + "\n");
	} finally {
		await browser.close();
	}
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
	main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
}
