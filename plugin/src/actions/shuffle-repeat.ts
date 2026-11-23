import { action, type KeyDownEvent } from "@elgato/streamdeck";

import type { AppleMusicState } from "../bridge/apple-music";
import { AppleMusicAction } from "./apple-music-action";
import { type KeyView, repeatView, shuffleView } from "./views";

/**
 * Shuffle on/off (two states). Shown crossed out when Apple Music's media session does not support
 * shuffle; pressing it then just flashes the alert.
 */
@action({ UUID: "com.panix8th.applemusic.shuffle" })
export class ShuffleAction extends AppleMusicAction {
	protected view(state: AppleMusicState): KeyView {
		return shuffleView(state);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		// In a multi-action the user picks "Off" (0) or "On" (1); only toggle when that is not the case yet.
		if (ev.payload.isInMultiAction && this.music.state.shuffle === (ev.payload.userDesiredState === 1)) {
			return;
		}

		await this.send(ev.action, "toggleShuffle");
	}
}

/**
 * Repeat: off → all → one → off, like Apple Music's own button. Crossed out when not supported.
 */
@action({ UUID: "com.panix8th.applemusic.repeat" })
export class RepeatAction extends AppleMusicAction {
	protected view(state: AppleMusicState): KeyView {
		return repeatView(state);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		await this.send(ev.action, "cycleRepeat");
	}
}
