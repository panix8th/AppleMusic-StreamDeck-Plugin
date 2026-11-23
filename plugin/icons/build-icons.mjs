#!/usr/bin/env node
/**
 * Builds every image the plugin ships from the SVG sources in this folder:
 *
 *   glyphs/*.svg  single-colour 24x24 glyphs (drawn with currentColor)
 *   logo.svg      full-colour plugin logo
 *
 * Output (PNG at @1x and @2x, the sizes Elgato's guidelines ask for):
 *   ../com.panix8th.applemusic.sdPlugin/imgs/plugin/marketplace.png      256 / 512   plugin icon
 *   ../com.panix8th.applemusic.sdPlugin/imgs/plugin/category-icon.png     28 / 56    white, transparent
 *   ../com.panix8th.applemusic.sdPlugin/imgs/actions/<action>/icon.png    20 / 40    white, transparent
 *   ../com.panix8th.applemusic.sdPlugin/imgs/actions/<action>/key-*.png   72 / 144   key images
 *   ../../docs/images/icons-preview.png                                             contact sheet
 *
 * Run with `npm run icons` from the plugin folder.
 */
import { Resvg } from "@resvg/resvg-js";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const ICONS_DIR = dirname(fileURLToPath(import.meta.url));
const IMGS_DIR = resolve(ICONS_DIR, "..", "com.panix8th.applemusic.sdPlugin", "imgs");
const PREVIEW_FILE = resolve(ICONS_DIR, "..", "..", "docs", "images", "icons-preview.png");

const COLORS = {
	keyTop: "#26262E",
	keyBottom: "#121216",
	glyph: "#FFFFFF",
	accent: "#FF4D6D",
};

/** Opacity of the glyph on dimmed keys ("Apple Music is not running", "not supported"). */
const DIMMED = 0.28;

/** Glyph size on a 144 px key. */
const KEY_GLYPH_SIZE = 76;

/**
 * Every action and the images it needs. `icon` is the action-list icon; every other entry is a key
 * image. Key options: glyph, color, opacity, indicator (accent dot = "on"), disabled (slash), centerY.
 */
const ACTIONS = {
	"now-playing": {
		icon: "note",
		keys: {
			// Shown until a cover arrives. Sits a little higher to leave room for the title at the bottom.
			key: { glyph: "note", centerY: 64 },
			"key-not-running": { glyph: "note", centerY: 64, opacity: DIMMED },
		},
	},
	"play-pause": {
		icon: "play-pause",
		keys: {
			"key-play": { glyph: "play" },
			"key-pause": { glyph: "pause" },
			"key-not-running": { glyph: "play", opacity: DIMMED },
		},
	},
	next: {
		icon: "next",
		keys: {
			key: { glyph: "next" },
			"key-not-running": { glyph: "next", opacity: DIMMED },
		},
	},
	previous: {
		icon: "previous",
		keys: {
			key: { glyph: "previous" },
			"key-not-running": { glyph: "previous", opacity: DIMMED },
		},
	},
	shuffle: {
		icon: "shuffle",
		keys: {
			"key-off": { glyph: "shuffle" },
			"key-on": { glyph: "shuffle", color: COLORS.accent, indicator: true },
			"key-disabled": { glyph: "shuffle", opacity: DIMMED, disabled: true },
			"key-not-running": { glyph: "shuffle", opacity: DIMMED },
		},
	},
	repeat: {
		icon: "repeat",
		keys: {
			"key-off": { glyph: "repeat" },
			"key-all": { glyph: "repeat", color: COLORS.accent, indicator: true },
			"key-one": { glyph: "repeat-one", color: COLORS.accent, indicator: true },
			"key-disabled": { glyph: "repeat", opacity: DIMMED, disabled: true },
			"key-not-running": { glyph: "repeat", opacity: DIMMED },
		},
	},
	// Stream Deck + dial (phase 2).
	volume: {
		icon: "volume",
		keys: {
			key: { glyph: "volume" },
			"key-muted": { glyph: "volume-muted", color: COLORS.accent },
			"key-not-running": { glyph: "volume", opacity: DIMMED },
		},
	},
};

const glyphCache = new Map();

/** Loads glyphs/<name>.svg and returns a function that places it with a transform. */
function glyph(name) {
	if (!glyphCache.has(name)) {
		const svg = readFileSync(join(ICONS_DIR, "glyphs", `${name}.svg`), "utf8");
		const open = svg.match(/<svg\b([^>]*)>/);
		if (!open) {
			throw new Error(`glyphs/${name}.svg has no <svg> element`);
		}

		const inner = svg.slice(open.index + open[0].length, svg.lastIndexOf("</svg>"));
		// Carry the root's presentation attributes over to the wrapping group.
		const presentation = [...open[1].matchAll(/([\w:-]+)="([^"]*)"/g)]
			.filter(([, key]) => ["fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin"].includes(key))
			.map(([, key, value]) => `${key}="${value}"`)
			.join(" ");

		glyphCache.set(name, (transform, extra = "") => `<g transform="${transform}" ${presentation} ${extra}>${inner}</g>`);
	}

	return glyphCache.get(name);
}

/** A 144x144 key image. */
function keySvg({ glyph: name, color = COLORS.glyph, opacity = 1, indicator = false, disabled = false, centerY = 72 }) {
	const scale = KEY_GLYPH_SIZE / 24;
	const transform = `translate(${72 - 12 * scale} ${centerY - 12 * scale}) scale(${scale})`;
	let content = glyph(name)(transform, `color="${color}" opacity="${opacity}"`);
	let defs = "";

	if (disabled) {
		// Cut a gap into the glyph along the slash, then draw the slash.
		const [x1, y1, x2, y2] = [72 - 30, centerY - 30, 72 + 30, centerY + 30];
		defs = `<mask id="slash" maskUnits="userSpaceOnUse" x="0" y="0" width="144" height="144">
			<rect width="144" height="144" fill="#fff"/>
			<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="#000" stroke-width="16" stroke-linecap="round"/>
		</mask>`;
		content = `<g mask="url(#slash)">${content}</g>
			<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${COLORS.glyph}" stroke-opacity="0.55" stroke-width="6" stroke-linecap="round"/>`;
	}

	if (indicator) {
		content += `<circle cx="72" cy="${centerY + KEY_GLYPH_SIZE / 2 + 15}" r="5" fill="${COLORS.accent}"/>`;
	}

	return `<svg xmlns="http://www.w3.org/2000/svg" width="144" height="144" viewBox="0 0 144 144">
		<defs>
			<linearGradient id="bg" x1="0" y1="0" x2="0" y2="144" gradientUnits="userSpaceOnUse">
				<stop offset="0" stop-color="${COLORS.keyTop}"/>
				<stop offset="1" stop-color="${COLORS.keyBottom}"/>
			</linearGradient>
			${defs}
		</defs>
		<rect width="144" height="144" fill="url(#bg)"/>
		${content}
	</svg>`;
}

/** Action-list icon: white glyph on transparent. */
function listIconSvg(name) {
	return `<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" viewBox="0 0 24 24">
		${glyph(name)("", `color="${COLORS.glyph}"`)}
	</svg>`;
}

/** Category icon: a key outline with the note, white on transparent. */
function categoryIconSvg() {
	return `<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56" viewBox="0 0 24 24">
		<rect x="2" y="2" width="20" height="20" rx="5.5" fill="none" stroke="${COLORS.glyph}" stroke-width="1.7"/>
		${glyph("note")("translate(12 12) scale(0.62) translate(-12 -12)", `color="${COLORS.glyph}"`)}
	</svg>`;
}

function render(svg, width) {
	return new Resvg(svg, {
		fitTo: { mode: "width", value: width },
		font: { loadSystemFonts: true, defaultFontFamily: "Inter" },
	})
		.render()
		.asPng();
}

const written = [];

/** Writes <name>.png at `size` and <name>@2x.png at twice the size. */
function writePair(directory, name, svg, size) {
	mkdirSync(directory, { recursive: true });
	for (const [suffix, width] of [["", size], ["@2x", size * 2]]) {
		const file = join(directory, `${name}${suffix}.png`);
		writeFileSync(file, render(svg, width));
		written.push(file);
	}
}

// ---------------------------------------------------------------------------------------------------

rmSync(join(IMGS_DIR, "actions"), { recursive: true, force: true });
rmSync(join(IMGS_DIR, "plugin"), { recursive: true, force: true });

const logo = readFileSync(join(ICONS_DIR, "logo.svg"), "utf8");
writePair(join(IMGS_DIR, "plugin"), "marketplace", logo, 256);
writePair(join(IMGS_DIR, "plugin"), "category-icon", categoryIconSvg(), 28);

const preview = [];
for (const [action, spec] of Object.entries(ACTIONS)) {
	const directory = join(IMGS_DIR, "actions", action);
	writePair(directory, "icon", listIconSvg(spec.icon), 20);

	const keys = [];
	for (const [name, options] of Object.entries(spec.keys)) {
		const svg = keySvg(options);
		writePair(directory, name, svg, 72);
		keys.push({ name, png: render(svg, 144) });
	}

	preview.push({ action, icon: render(listIconSvg(spec.icon), 40), keys });
}

writePreview(preview);

console.log(`Wrote ${written.length} PNG files to ${relative(process.cwd(), IMGS_DIR)}`);
console.log(`Preview: ${relative(process.cwd(), PREVIEW_FILE)}`);

/** Contact sheet with every generated image, for the README and for reviewing changes. */
function writePreview(rows) {
	const cell = 168;
	const left = 190;
	const top = 230;
	const rowHeight = 196;
	const columns = Math.max(...rows.map((row) => row.keys.length));
	const width = left + 96 + columns * cell + 24;
	const height = top + rows.length * rowHeight + 10;
	const dataUrl = (png) => `data:image/png;base64,${png.toString("base64")}`;
	const font = `font-family="Inter, Segoe UI, Arial, sans-serif"`;

	const parts = [
		`<rect width="${width}" height="${height}" fill="#1C1C1F"/>`,
		`<image href="${dataUrl(render(logo, 160))}" x="24" y="24" width="160" height="160"/>`,
		`<text x="208" y="86" ${font} font-size="34" font-weight="700" fill="#FFFFFF">Apple Music for Stream Deck</text>`,
		`<text x="208" y="122" ${font} font-size="18" fill="#A0A0AA">Keys 72/144 px · action list icons 20/40 px · category 28/56 px · plugin icon 256/512 px</text>`,
		`<rect x="208" y="140" width="56" height="56" rx="10" fill="#2D2D33"/>`,
		`<image href="${dataUrl(render(categoryIconSvg(), 40))}" x="216" y="148" width="40" height="40"/>`,
		`<text x="276" y="174" ${font} font-size="16" fill="#A0A0AA">category icon</text>`,
	];

	rows.forEach((row, index) => {
		const y = top + index * rowHeight;
		parts.push(`<text x="24" y="${y + 78}" ${font} font-size="20" font-weight="600" fill="#FFFFFF">${row.action}</text>`);
		parts.push(`<rect x="${left - 40}" y="${y + 48}" width="64" height="64" rx="10" fill="#2D2D33"/>`);
		parts.push(`<image href="${dataUrl(row.icon)}" x="${left - 28}" y="${y + 60}" width="40" height="40"/>`);
		row.keys.forEach((key, column) => {
			const x = left + 96 + column * cell;
			parts.push(`<rect x="${x - 2}" y="${y - 2}" width="148" height="148" rx="20" fill="#000"/>`);
			parts.push(`<clipPath id="c${index}-${column}"><rect x="${x}" y="${y}" width="144" height="144" rx="18"/></clipPath>`);
			parts.push(`<image href="${dataUrl(key.png)}" x="${x}" y="${y}" width="144" height="144" clip-path="url(#c${index}-${column})"/>`);
			parts.push(`<text x="${x + 72}" y="${y + 170}" ${font} font-size="15" fill="#A0A0AA" text-anchor="middle">${key.name}</text>`);
		});
	});

	const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">${parts.join("")}</svg>`;
	mkdirSync(dirname(PREVIEW_FILE), { recursive: true });
	writeFileSync(PREVIEW_FILE, render(svg, width));
}
