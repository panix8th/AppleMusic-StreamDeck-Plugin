import { describe, expect, it } from "vitest";

import { type AppleMusicState, DISCONNECTED } from "../src/bridge/apple-music";
import {
	KeyImages,
	NOT_RUNNING_TITLE,
	nowPlayingView,
	playPauseView,
	repeatView,
	shuffleView,
	skipView,
} from "../src/actions/views";

const ALL_SUPPORTED = { play: true, pause: true, playPause: true, next: true, previous: true, shuffle: true, repeat: true };

/** Apple Music playing a song, as reported on a real machine (shuffle/repeat not supported). */
const playing: AppleMusicState = {
	...DISCONNECTED,
	connected: true,
	appRunning: true,
	hasSession: true,
	title: "HORSESHOE",
	artist: "Tate McRae",
	album: "SO CLOSE TO WHAT??? (deluxe)",
	isPlaying: true,
	status: "playing",
	albumArtId: "1e83feea40e9dbd7",
	albumArt: "data:image/png;base64,iVBORw0KGgo=",
	supports: { play: false, pause: true, playPause: true, next: true, previous: true, shuffle: false, repeat: false },
};

const notRunning: AppleMusicState = { ...DISCONNECTED, connected: true };
const runningWithoutSession: AppleMusicState = { ...DISCONNECTED, connected: true, appRunning: true };

describe("Now Playing", () => {
	it("shows the cover and the title", () => {
		expect(nowPlayingView(playing)).toEqual({ image: playing.albumArt, title: "HORSESHOE" });
	});

	it("falls back to the manifest image without a cover", () => {
		expect(nowPlayingView({ ...playing, albumArt: undefined }).image).toBeUndefined();
	});

	it("is dimmed with a note when Apple Music is not running", () => {
		expect(nowPlayingView(notRunning)).toEqual({ image: KeyImages.nowPlaying.notRunning, title: NOT_RUNNING_TITLE });
	});

	it("is dimmed without text while the helper is not connected", () => {
		expect(nowPlayingView(DISCONNECTED)).toEqual({ image: KeyImages.nowPlaying.notRunning, title: "" });
	});
});

describe("Play/Pause", () => {
	it("follows the playback state", () => {
		expect(playPauseView(playing)).toEqual({ image: undefined, state: 1 });
		expect(playPauseView({ ...playing, isPlaying: false })).toEqual({ image: undefined, state: 0 });
	});

	it("is dimmed when Apple Music is not running or the helper is down", () => {
		expect(playPauseView(notRunning)).toEqual({ image: KeyImages.playPause.notRunning, state: 0 });
		expect(playPauseView(DISCONNECTED)).toEqual({ image: KeyImages.playPause.notRunning, state: 0 });
	});

	it("stays usable when Apple Music runs without a session yet", () => {
		expect(playPauseView(runningWithoutSession)).toEqual({ image: undefined, state: 0 });
	});
});

describe("Next / Previous", () => {
	it("uses the manifest image while available and the dimmed one otherwise", () => {
		expect(skipView(playing, "next")).toEqual({ image: undefined });
		expect(skipView(notRunning, "next")).toEqual({ image: KeyImages.next.notRunning });
		expect(skipView(notRunning, "previous")).toEqual({ image: KeyImages.previous.notRunning });
	});
});

describe("Shuffle", () => {
	it("is crossed out when the session does not support it", () => {
		expect(shuffleView(playing)).toEqual({ image: KeyImages.shuffle.disabled, state: 0 });
	});

	it("shows off/on when supported", () => {
		const supported = { ...playing, supports: ALL_SUPPORTED };
		expect(shuffleView({ ...supported, shuffle: false })).toEqual({ image: undefined, state: 0 });
		expect(shuffleView({ ...supported, shuffle: true })).toEqual({ image: undefined, state: 1 });
	});

	it("is dimmed when Apple Music is not running", () => {
		expect(shuffleView(notRunning)).toEqual({ image: KeyImages.shuffle.notRunning, state: 0 });
	});

	it("does not claim 'unsupported' before there is a session to ask", () => {
		expect(shuffleView(runningWithoutSession)).toEqual({ image: undefined, state: 0 });
	});
});

describe("Repeat", () => {
	it("is crossed out when the session does not support it", () => {
		expect(repeatView(playing)).toEqual({ image: KeyImages.repeat.disabled });
	});

	it("shows off, all and one when supported", () => {
		const supported = { ...playing, supports: ALL_SUPPORTED };
		expect(repeatView({ ...supported, repeat: "off" })).toEqual({ image: undefined });
		expect(repeatView({ ...supported, repeat: "all" })).toEqual({ image: KeyImages.repeat.all });
		expect(repeatView({ ...supported, repeat: "one" })).toEqual({ image: KeyImages.repeat.one });
	});

	it("is dimmed when Apple Music is not running", () => {
		expect(repeatView(notRunning)).toEqual({ image: KeyImages.repeat.notRunning });
	});
});
