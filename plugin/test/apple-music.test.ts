import { EventEmitter } from "node:events";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AppleMusic, type AppleMusicState } from "../src/bridge/apple-music";
import type { Command, HelperMessage, StateMessage } from "../src/bridge/protocol";
import { recordingLog } from "./recording-log";

class FakeHelper extends EventEmitter {
	sent: Command[] = [];
	running = true;

	send(command: Command): boolean {
		if (!this.running) {
			return false;
		}

		this.sent.push(command);
		return true;
	}

	receive(message: HelperMessage): void {
		this.emit("message", message);
	}
}

function stateMessage(overrides: Partial<StateMessage> = {}): StateMessage {
	return {
		type: "state",
		appRunning: true,
		hasSession: true,
		title: "HORSESHOE",
		artist: "Tate McRae",
		album: "SO CLOSE TO WHAT??? (deluxe)",
		isPlaying: true,
		status: "playing",
		shuffle: null,
		repeat: null,
		position: 88.2,
		duration: 181,
		timestamp: 1_795_451_701_271,
		albumArtId: "1e83feea40e9dbd7",
		supports: { play: false, pause: true, playPause: true, next: true, previous: true, shuffle: false, repeat: false },
		sourceAppId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
		...overrides,
	};
}

function setup() {
	const helper = new FakeHelper();
	const music = new AppleMusic(helper, recordingLog(), 1_000);
	const changes: AppleMusicState[] = [];
	music.on("change", (state) => changes.push(state));
	return { helper, music, changes };
}

afterEach(() => {
	vi.useRealTimers();
});

describe("AppleMusic", () => {
	it("starts disconnected", () => {
		const { music } = setup();
		expect(music.state.connected).toBe(false);
		expect(music.state.appRunning).toBe(false);
	});

	it("turns state messages into state, with the cover as a data URL", () => {
		const { helper, music, changes } = setup();
		helper.receive(stateMessage({ albumArt: "iVBORw0KGgo=" }));

		expect(changes).toHaveLength(1);
		expect(music.state).toMatchObject({
			connected: true,
			appRunning: true,
			title: "HORSESHOE",
			isPlaying: true,
			albumArtId: "1e83feea40e9dbd7",
			albumArt: "data:image/png;base64,iVBORw0KGgo=",
		});
	});

	it("keeps the cover when later messages omit it for the same id", () => {
		const { helper, music } = setup();
		helper.receive(stateMessage({ albumArt: "iVBORw0KGgo=" }));
		helper.receive(stateMessage({ isPlaying: false, status: "paused" }));

		expect(music.state.isPlaying).toBe(false);
		expect(music.state.albumArt).toBe("data:image/png;base64,iVBORw0KGgo=");
		expect(helper.sent).toHaveLength(0);
	});

	it("reuses a cached cover when going back to an earlier track", () => {
		const { helper, music } = setup();
		helper.receive(stateMessage({ albumArtId: "a", albumArt: "QQ==" }));
		helper.receive(stateMessage({ albumArtId: "b", albumArt: "Qg==" }));
		helper.receive(stateMessage({ albumArtId: "a" }));

		expect(music.state.albumArt).toBe("data:image/png;base64,QQ==");
		expect(helper.sent).toHaveLength(0);
	});

	it("drops the cover when the track has none", () => {
		const { helper, music } = setup();
		helper.receive(stateMessage({ albumArt: "iVBORw0KGgo=" }));
		helper.receive(stateMessage({ albumArtId: null }));

		expect(music.state.albumArt).toBeUndefined();
	});

	it("asks for a refresh once when a cover id arrives without the image", () => {
		const { helper, music } = setup();
		helper.receive(stateMessage({ albumArtId: "unknown" }));
		helper.receive(stateMessage({ albumArtId: "unknown", position: 90 }));

		expect(music.state.albumArt).toBeUndefined();
		expect(helper.sent.map((command) => command.cmd)).toEqual(["refresh"]);
	});

	it("resolves commands with the helper's result", async () => {
		const { helper, music } = setup();
		const pending = music.command("next");
		const [command] = helper.sent;
		expect(command.cmd).toBe("next");

		helper.receive({ type: "result", id: command.id, cmd: "next", ok: false, error: "no-session" });
		await expect(pending).resolves.toEqual({ ok: false, error: "no-session" });
	});

	it("gives every command its own id", () => {
		const { helper, music } = setup();
		void music.command("play");
		void music.command("pause");
		expect(new Set(helper.sent.map((command) => command.id)).size).toBe(2);
	});

	it("times out commands that get no answer", async () => {
		vi.useFakeTimers();
		const { music } = setup();
		const pending = music.command("focus");
		vi.advanceTimersByTime(1_001);
		await expect(pending).resolves.toEqual({ ok: false, error: "timeout" });
	});

	it("fails commands right away when the helper is not running", async () => {
		const { helper, music } = setup();
		helper.running = false;
		await expect(music.command("play")).resolves.toEqual({ ok: false, error: "helper-not-running" });
	});

	it("goes back to disconnected and fails pending commands when the helper exits", async () => {
		const { helper, music, changes } = setup();
		helper.receive(stateMessage());
		const pending = music.command("togglePlayPause");

		helper.emit("exited", 1, null);
		await expect(pending).resolves.toEqual({ ok: false, error: "helper-exited" });
		expect(music.state.connected).toBe(false);
		expect(changes.at(-1)?.connected).toBe(false);
	});
});
