import { EventEmitter } from "node:events";

import type { Log } from "../log";
import type { Capabilities, Command, CommandName, HelperMessage, RepeatMode, StateMessage } from "./protocol";

/** What the actions render. */
export type AppleMusicState = {
	/** The helper is running and has reported a state. */
	connected: boolean;
	/** Apple Music has a media session or its process is running. */
	appRunning: boolean;
	/** Apple Music registered a media session (needed for playback commands). */
	hasSession: boolean;
	title: string;
	artist: string;
	album: string;
	isPlaying: boolean;
	status: string;
	shuffle: boolean | null;
	repeat: RepeatMode | null;
	position: number;
	duration: number;
	timestamp: number;
	supports: Capabilities;
	albumArtId: string | null;
	/** `data:image/png;base64,...` of the current cover, when known. */
	albumArt: string | undefined;
};

export type CommandResult = { ok: boolean; error?: string };

/** The parts of HelperProcess this class needs (an interface so tests can use a fake). */
export type HelperConnection = {
	on(event: "message", listener: (message: HelperMessage) => void): unknown;
	on(event: "exited", listener: () => void): unknown;
	send(command: Command): boolean;
};

const NO_CAPABILITIES: Capabilities = {
	play: false,
	pause: false,
	playPause: false,
	next: false,
	previous: false,
	shuffle: false,
	repeat: false,
};

export const DISCONNECTED: AppleMusicState = {
	connected: false,
	appRunning: false,
	hasSession: false,
	title: "",
	artist: "",
	album: "",
	isPlaying: false,
	status: "none",
	shuffle: null,
	repeat: null,
	position: 0,
	duration: 0,
	timestamp: 0,
	supports: NO_CAPABILITIES,
	albumArtId: null,
	albumArt: undefined,
};

const ART_CACHE_SIZE = 16;

/**
 * Apple Music as the actions see it: the latest state reported by the helper (with the cover as a data URL)
 * and commands that resolve with the helper's answer.
 */
export class AppleMusic extends EventEmitter<{ change: [state: AppleMusicState] }> {
	readonly #helper: HelperConnection;
	readonly #log: Log;
	readonly #commandTimeoutMs: number;
	readonly #pending = new Map<number, { resolve: (result: CommandResult) => void; timer: NodeJS.Timeout }>();
	readonly #artCache = new Map<string, string>();
	#state: AppleMusicState = DISCONNECTED;
	#nextId = 1;
	#artRequestedFor: string | undefined;

	constructor(helper: HelperConnection, log: Log, commandTimeoutMs = 6_000) {
		super();
		this.#helper = helper;
		this.#log = log;
		this.#commandTimeoutMs = commandTimeoutMs;
		helper.on("message", (message) => this.#onMessage(message));
		helper.on("exited", () => this.#onHelperExited());
	}

	get state(): AppleMusicState {
		return this.#state;
	}

	/** Sends a command; resolves with the helper's answer (never rejects). */
	command(cmd: CommandName): Promise<CommandResult> {
		const id = this.#nextId++;
		if (!this.#helper.send({ id, cmd })) {
			return Promise.resolve({ ok: false, error: "helper-not-running" });
		}

		this.#log.debug(`Command ${id}: ${cmd}`);
		return new Promise((resolve) => {
			const timer = setTimeout(() => {
				this.#pending.delete(id);
				resolve({ ok: false, error: "timeout" });
			}, this.#commandTimeoutMs);
			this.#pending.set(id, { resolve, timer });
		});
	}

	/** Asks the helper to re-check everything and resend the full state. */
	refresh(): void {
		void this.command("refresh");
	}

	#onMessage(message: HelperMessage): void {
		switch (message.type) {
			case "hello":
				this.#log.info(`Connected to AppleMusicBridge ${message.version} (pid ${message.pid}).`);
				break;
			case "state":
				this.#onState(message);
				break;
			case "result": {
				const id = typeof message.id === "number" ? message.id : Number.NaN;
				const pending = this.#pending.get(id);
				if (pending) {
					this.#pending.delete(id);
					clearTimeout(pending.timer);
					this.#log.debug(`Command ${id} (${message.cmd}): ${message.ok ? "ok" : message.error}`);
					pending.resolve({ ok: message.ok, error: message.error });
				}

				break;
			}
		}
	}

	#onState(message: StateMessage): void {
		let albumArt: string | undefined;
		if (message.albumArtId !== null) {
			if (message.albumArt) {
				albumArt = `data:image/png;base64,${message.albumArt}`;
				this.#cacheArt(message.albumArtId, albumArt);
			} else {
				albumArt = this.#artCache.get(message.albumArtId);
				if (albumArt === undefined && this.#artRequestedFor !== message.albumArtId) {
					// We missed the cover for this id (should not happen); ask once for a full state.
					this.#artRequestedFor = message.albumArtId;
					this.refresh();
				}
			}
		}

		this.#state = {
			connected: true,
			appRunning: message.appRunning,
			hasSession: message.hasSession,
			title: message.title,
			artist: message.artist,
			album: message.album,
			isPlaying: message.isPlaying,
			status: message.status,
			shuffle: message.shuffle,
			repeat: message.repeat,
			position: message.position,
			duration: message.duration,
			timestamp: message.timestamp,
			supports: message.supports ?? NO_CAPABILITIES,
			albumArtId: message.albumArtId,
			albumArt,
		};

		this.emit("change", this.#state);
	}

	#cacheArt(id: string, dataUrl: string): void {
		this.#artCache.delete(id);
		this.#artCache.set(id, dataUrl);
		while (this.#artCache.size > ART_CACHE_SIZE) {
			const oldest = this.#artCache.keys().next().value;
			if (oldest === undefined) {
				break;
			}

			this.#artCache.delete(oldest);
		}
	}

	#onHelperExited(): void {
		for (const [id, pending] of this.#pending) {
			clearTimeout(pending.timer);
			pending.resolve({ ok: false, error: "helper-exited" });
			this.#pending.delete(id);
		}

		this.#state = DISCONNECTED;
		this.emit("change", this.#state);
	}
}
