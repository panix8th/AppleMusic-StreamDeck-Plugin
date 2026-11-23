import { action, type KeyDownEvent } from "@elgato/streamdeck";

import type { AppleMusicState } from "../bridge/apple-music";
import { AppleMusicAction } from "./apple-music-action";
import { type KeyView, playPauseView } from "./views";

/**
 * Play/pause with two states (0 = paused, shows ▶; 1 = playing, shows ❚❚). Automatic state toggling is off in
 * the manifest: the state always follows Apple Music, however playback was changed.
 */
@action({ UUID: "com.panix8th.applemusic.play-pause" })
export class PlayPauseAction extends AppleMusicAction {
	protected view(state: AppleMusicState): KeyView {
		return playPauseView(state);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		// In a multi-action the user picks a state: "Play" (0) or "Pause" (1).
		const command = ev.payload.isInMultiAction
			? ev.payload.userDesiredState === 1
				? "pause"
				: "play"
			: "togglePlayPause";
		await this.send(ev.action, command);
	}
}
