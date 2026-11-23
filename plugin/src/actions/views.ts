import type { AppleMusicState } from "../bridge/apple-music";

/**
 * What a key should show. Pure functions of the Apple Music state, so they can be tested without
 * Stream Deck.
 */
export type KeyView = {
	/** Image path or data URL; undefined = the manifest image (of each state). */
	image: string | undefined;
	/** State index for two-state actions. */
	state?: 0 | 1;
	/** Key title; undefined = leave the title alone. */
	title?: string;
};

const ACTIONS = "imgs/actions";

/**
 * Key images set at runtime. Stream Deck takes a single size here, so these are the @2x (144 px) images;
 * it scales them down for 72 px keys.
 */
export const KeyImages = {
	nowPlaying: {
		notRunning: `${ACTIONS}/now-playing/key-not-running@2x.png`,
	},
	playPause: {
		notRunning: `${ACTIONS}/play-pause/key-not-running@2x.png`,
	},
	next: {
		notRunning: `${ACTIONS}/next/key-not-running@2x.png`,
	},
	previous: {
		notRunning: `${ACTIONS}/previous/key-not-running@2x.png`,
	},
	shuffle: {
		disabled: `${ACTIONS}/shuffle/key-disabled@2x.png`,
		notRunning: `${ACTIONS}/shuffle/key-not-running@2x.png`,
	},
	repeat: {
		all: `${ACTIONS}/repeat/key-all@2x.png`,
		one: `${ACTIONS}/repeat/key-one@2x.png`,
		disabled: `${ACTIONS}/repeat/key-disabled@2x.png`,
		notRunning: `${ACTIONS}/repeat/key-not-running@2x.png`,
	},
} as const;

export const NOT_RUNNING_TITLE = "Not\nrunning";

/** Apple Music can be controlled at all (the helper runs and the app is open). */
export function isAvailable(state: AppleMusicState): boolean {
	return state.connected && state.appRunning;
}

export function nowPlayingView(state: AppleMusicState): KeyView & { title: string } {
	if (!state.connected) {
		return { image: KeyImages.nowPlaying.notRunning, title: "" };
	}

	if (!state.appRunning) {
		return { image: KeyImages.nowPlaying.notRunning, title: NOT_RUNNING_TITLE };
	}

	// Cover when there is one, otherwise the note from the manifest.
	return { image: state.albumArt, title: state.title };
}

export function playPauseView(state: AppleMusicState): KeyView {
	if (!isAvailable(state)) {
		return { image: KeyImages.playPause.notRunning, state: 0 };
	}

	return { image: undefined, state: state.isPlaying ? 1 : 0 };
}

export function skipView(state: AppleMusicState, direction: "next" | "previous"): KeyView {
	return { image: isAvailable(state) ? undefined : KeyImages[direction].notRunning };
}

export function shuffleView(state: AppleMusicState): KeyView {
	if (!isAvailable(state)) {
		return { image: KeyImages.shuffle.notRunning, state: 0 };
	}

	// Only judge support once there is a session to ask; before that, show the normal key.
	if (state.hasSession && !state.supports.shuffle) {
		return { image: KeyImages.shuffle.disabled, state: 0 };
	}

	return { image: undefined, state: state.shuffle ? 1 : 0 };
}

export function repeatView(state: AppleMusicState): KeyView {
	if (!isAvailable(state)) {
		return { image: KeyImages.repeat.notRunning };
	}

	if (state.hasSession && !state.supports.repeat) {
		return { image: KeyImages.repeat.disabled };
	}

	switch (state.repeat) {
		case "all":
			return { image: KeyImages.repeat.all };
		case "one":
			return { image: KeyImages.repeat.one };
		default:
			return { image: undefined };
	}
}
