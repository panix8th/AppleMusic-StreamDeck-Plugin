/** Characters that fit on a key at Stream Deck's default title size. */
export const MARQUEE_WIDTH = 10;

const GAP = "   ";

export type MarqueeOptions = {
	/** Visible characters. */
	width: number;
	/** Time between steps. Stream Deck asks for at most 10 updates per second per key. */
	intervalMs: number;
	/** Steps to rest at the start of each pass, so the beginning of the title is readable. */
	holdSteps: number;
};

const DEFAULT_OPTIONS: MarqueeOptions = { width: MARQUEE_WIDTH, intervalMs: 400, holdSteps: 4 };

/** Whether `text` is too long for the key and has to scroll. Multi-line titles never scroll. */
export function needsScrolling(text: string, width = MARQUEE_WIDTH): boolean {
	return !text.includes("\n") && Array.from(text).length > width;
}

/** The visible part of `text` after scrolling `offset` characters (wrapping around with a gap). */
export function marqueeFrame(text: string, offset: number, width = MARQUEE_WIDTH): string {
	if (!needsScrolling(text, width)) {
		return text;
	}

	// Array.from splits by code point, so emoji and other astral characters are never cut in half.
	const loop = [...Array.from(text), ...Array.from(GAP)];
	const start = offset % loop.length;
	return [...loop, ...loop].slice(start, start + width).join("");
}

/** Scrolls a title that is too long for the key; short titles are shown as they are. */
export class Marquee {
	readonly #show: (frame: string) => void;
	readonly #options: MarqueeOptions;
	#text: string | undefined;
	#offset = 0;
	#hold = 0;
	#timer: NodeJS.Timeout | undefined;

	constructor(show: (frame: string) => void, options: Partial<MarqueeOptions> = {}) {
		this.#show = show;
		this.#options = { ...DEFAULT_OPTIONS, ...options };
	}

	/** Shows `text`, restarting the scroll if it changed. `force` re-shows the same text (key reappeared). */
	set(text: string, force = false): void {
		if (!force && text === this.#text) {
			return;
		}

		this.#text = text;
		this.#offset = 0;
		this.#hold = this.#options.holdSteps;
		this.#show(marqueeFrame(text, 0, this.#options.width));

		if (needsScrolling(text, this.#options.width)) {
			this.#timer ??= setInterval(() => this.#step(), this.#options.intervalMs);
		} else {
			this.#clearTimer();
		}
	}

	stop(): void {
		this.#clearTimer();
		this.#text = undefined;
	}

	#step(): void {
		const text = this.#text;
		if (text === undefined) {
			return;
		}

		if (this.#hold > 0) {
			this.#hold--;
			return;
		}

		const loopLength = Array.from(text).length + GAP.length;
		this.#offset = (this.#offset + 1) % loopLength;
		if (this.#offset === 0) {
			this.#hold = this.#options.holdSteps;
		}

		this.#show(marqueeFrame(text, this.#offset, this.#options.width));
	}

	#clearTimer(): void {
		clearInterval(this.#timer);
		this.#timer = undefined;
	}
}
