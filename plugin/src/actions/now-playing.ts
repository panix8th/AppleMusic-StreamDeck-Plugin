import { action, type KeyAction, type KeyDownEvent, type WillAppearEvent, type WillDisappearEvent } from "@elgato/streamdeck";
import type { JsonObject } from "@elgato/utils";

import type { AppleMusicState } from "../bridge/apple-music";
import { AppleMusicAction } from "./apple-music-action";
import { Marquee } from "./marquee";
import { type KeyView, nowPlayingView } from "./views";

/**
 * Album cover as the key image and the song title as the key title (scrolling when it is too long).
 * Pressing the key brings the Apple Music window to the front.
 */
@action({ UUID: "com.panix8th.applemusic.now-playing" })
export class NowPlayingAction extends AppleMusicAction {
	readonly #marquees = new Map<string, Marquee>();

	protected view(state: AppleMusicState): KeyView {
		// The title is driven by the marquee, not by the generic renderer.
		return { image: nowPlayingView(state).image };
	}

	override async onWillAppear(ev: WillAppearEvent): Promise<void> {
		await super.onWillAppear(ev);
		if (ev.action.isKey()) {
			this.#marquee(ev.action).set(nowPlayingView(this.music.state).title, true);
		}
	}

	override onWillDisappear(ev: WillDisappearEvent): void {
		super.onWillDisappear(ev);
		this.#marquees.get(ev.action.id)?.stop();
		this.#marquees.delete(ev.action.id);
	}

	override async onKeyDown(ev: KeyDownEvent): Promise<void> {
		await this.send(ev.action, "focus");
	}

	protected override async renderAll(state: AppleMusicState): Promise<void> {
		await super.renderAll(state);
		const title = nowPlayingView(state).title;
		for (const key of this.actions.toArray()) {
			if (key.isKey()) {
				this.#marquee(key).set(title);
			}
		}
	}

	#marquee(key: KeyAction<JsonObject>): Marquee {
		let marquee = this.#marquees.get(key.id);
		if (!marquee) {
			marquee = new Marquee((frame) => {
				key.setTitle(frame).catch(() => {
					// The key went away between two frames; onWillDisappear stops the marquee.
				});
			});
			this.#marquees.set(key.id, marquee);
		}

		return marquee;
	}
}
