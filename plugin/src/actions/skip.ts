import { action, type KeyDownEvent } from "@elgato/streamdeck";

import type { AppleMusicState } from "../bridge/apple-music";
import { AppleMusicAction } from "./apple-music-action";
import { type KeyView, skipView } from "./views";

/** Next track. */
@action({ UUID: "com.panix8th.applemusic.next" })
export class NextTrackAction extends AppleMusicAction {
	protected view(state: AppleMusicState): KeyView {
		return skipView(state, "next");
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		await this.send(ev.action, "next");
	}
}

/** Previous track (Apple Music restarts the current song first when it has been playing for a while). */
@action({ UUID: "com.panix8th.applemusic.previous" })
export class PreviousTrackAction extends AppleMusicAction {
	protected view(state: AppleMusicState): KeyView {
		return skipView(state, "previous");
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		await this.send(ev.action, "previous");
	}
}
