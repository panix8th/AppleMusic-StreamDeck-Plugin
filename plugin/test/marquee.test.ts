import { afterEach, describe, expect, it, vi } from "vitest";

import { Marquee, marqueeFrame, needsScrolling } from "../src/actions/marquee";

afterEach(() => {
	vi.useRealTimers();
});

describe("marqueeFrame", () => {
	it("leaves short titles alone", () => {
		expect(marqueeFrame("HORSESHOE", 5)).toBe("HORSESHOE");
		expect(needsScrolling("HORSESHOE")).toBe(false);
	});

	it("shows a 10 character window that wraps around with a gap", () => {
		const title = "Bohemian Rhapsody";
		expect(marqueeFrame(title, 0)).toBe("Bohemian R");
		expect(marqueeFrame(title, 9)).toBe("Rhapsody  ");
		expect(marqueeFrame(title, 15)).toBe("dy   Bohem");
		expect(marqueeFrame(title, title.length + 3)).toBe("Bohemian R");
	});

	it("never splits emoji", () => {
		const frame = marqueeFrame("🎵🎶 Long title with emoji", 1);
		expect(frame.startsWith("🎶")).toBe(true);
		expect(Array.from(frame)).toHaveLength(10);
	});

	it("never scrolls multi-line text", () => {
		expect(needsScrolling("Not\nrunning at all")).toBe(false);
	});
});

describe("Marquee", () => {
	it("shows short text once and does not tick", () => {
		vi.useFakeTimers();
		const frames: string[] = [];
		const marquee = new Marquee((frame) => frames.push(frame));
		marquee.set("Short");
		vi.advanceTimersByTime(10_000);
		expect(frames).toEqual(["Short"]);
	});

	it("holds at the start, then scrolls one character per step", () => {
		vi.useFakeTimers();
		const frames: string[] = [];
		const marquee = new Marquee((frame) => frames.push(frame), { intervalMs: 100, holdSteps: 2 });
		marquee.set("Bohemian Rhapsody");

		vi.advanceTimersByTime(200); // two held steps
		expect(frames).toEqual(["Bohemian R"]);

		vi.advanceTimersByTime(300);
		expect(frames).toEqual(["Bohemian R", "ohemian Rh", "hemian Rha", "emian Rhap"]);
		marquee.stop();
	});

	it("restarts from the beginning when the title changes, and ignores unchanged titles", () => {
		vi.useFakeTimers();
		const frames: string[] = [];
		const marquee = new Marquee((frame) => frames.push(frame), { intervalMs: 100, holdSteps: 0 });
		marquee.set("Bohemian Rhapsody");
		vi.advanceTimersByTime(300);
		marquee.set("Bohemian Rhapsody");
		marquee.set("Another Long Song Title");
		expect(frames.at(-1)).toBe("Another Lo");
		marquee.set("Short");
		vi.advanceTimersByTime(1_000);
		expect(frames.at(-1)).toBe("Short");
	});

	it("re-shows the same text when forced (the key reappeared)", () => {
		const frames: string[] = [];
		const marquee = new Marquee((frame) => frames.push(frame));
		marquee.set("Short");
		marquee.set("Short", true);
		expect(frames).toEqual(["Short", "Short"]);
	});

	it("stops ticking after stop()", () => {
		vi.useFakeTimers();
		const frames: string[] = [];
		const marquee = new Marquee((frame) => frames.push(frame), { intervalMs: 100, holdSteps: 0 });
		marquee.set("Bohemian Rhapsody");
		marquee.stop();
		vi.advanceTimersByTime(1_000);
		expect(frames).toEqual(["Bohemian R"]);
	});
});
