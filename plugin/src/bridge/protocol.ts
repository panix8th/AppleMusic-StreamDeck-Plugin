/**
 * Messages exchanged with AppleMusicBridge.exe, one JSON object per line.
 * Source of truth: docs/PROTOCOL.md (and helper/AppleMusicBridge/Protocol.cs).
 */

export type RepeatMode = "off" | "all" | "one";

export type Capabilities = {
	play: boolean;
	pause: boolean;
	playPause: boolean;
	next: boolean;
	previous: boolean;
	shuffle: boolean;
	repeat: boolean;
};

export type HelloMessage = {
	type: "hello";
	version: string;
	pid: number;
	debug: boolean;
};

export type StateMessage = {
	type: "state";
	appRunning: boolean;
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
	/** Always present; null = no cover. */
	albumArtId: string | null;
	/** Base64 PNG; only present when albumArtId changed (or after a refresh). */
	albumArt?: string;
	supports: Capabilities;
	sourceAppId: string | null;
};

export type ResultMessage = {
	type: "result";
	id: unknown;
	cmd: string | null;
	ok: boolean;
	error?: string;
};

export type HelperMessage = HelloMessage | StateMessage | ResultMessage;

export type CommandName =
	| "play"
	| "pause"
	| "togglePlayPause"
	| "next"
	| "previous"
	| "toggleShuffle"
	| "cycleRepeat"
	| "focus"
	| "refresh"
	| "ping";

export type Command = {
	id: number;
	cmd: CommandName;
};

/** Parses one stdout line; returns undefined for anything that is not a protocol message. */
export function parseHelperMessage(line: string): HelperMessage | undefined {
	let value: unknown;
	try {
		value = JSON.parse(line);
	} catch {
		return undefined;
	}

	if (typeof value !== "object" || value === null || !("type" in value)) {
		return undefined;
	}

	switch ((value as { type: unknown }).type) {
		case "hello":
		case "state":
		case "result":
			return value as HelperMessage;
		default:
			return undefined;
	}
}
