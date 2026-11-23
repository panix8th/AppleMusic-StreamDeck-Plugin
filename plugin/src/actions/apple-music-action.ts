import streamDeck, { type KeyAction, SingletonAction, type WillAppearEvent, type WillDisappearEvent } from "@elgato/streamdeck";
import type { JsonObject } from "@elgato/utils";

import type { AppleMusic, AppleMusicState } from "../bridge/apple-music";
import type { CommandName } from "../bridge/protocol";
import type { KeyView } from "./views";

const log = streamDeck.logger.createScope("Actions");

/**
 * Base for all keys: re-renders every visible key of the action when the Apple Music state changes, and only
 * sends Stream Deck what actually changed.
 */
export abstract class AppleMusicAction extends SingletonAction {
	protected readonly music: AppleMusic;
	readonly #applied = new Map<string, KeyView>();

	constructor(music: AppleMusic) {
		super();
		this.music = music;
		music.on("change", (state) => void this.renderAll(state));
	}

	/** What the key should show for `state`. */
	protected abstract view(state: AppleMusicState): KeyView;

	override async onWillAppear(ev: WillAppearEvent): Promise<void> {
		if (ev.action.isKey()) {
			await this.render(ev.action, this.music.state, true);
		}
	}

	override onWillDisappear(ev: WillDisappearEvent): void | Promise<void> {
		this.#applied.delete(ev.action.id);
	}

	protected async renderAll(state: AppleMusicState): Promise<void> {
		for (const action of this.actions.toArray()) {
			if (action.isKey()) {
				await this.render(action, state, false);
			}
		}
	}

	/** Applies the view for `state`; `force` resends everything (the key just appeared). */
	protected async render(action: KeyAction<JsonObject>, state: AppleMusicState, force: boolean): Promise<void> {
		const view = this.view(state);
		const last = force ? undefined : this.#applied.get(action.id);
		this.#applied.set(action.id, view);

		try {
			if (last === undefined || last.image !== view.image) {
				await action.setImage(view.image);
			}

			if (view.state !== undefined && (last === undefined || last.state !== view.state)) {
				await action.setState(view.state);
			}

			if (view.title !== undefined && (last === undefined || last.title !== view.title)) {
				await action.setTitle(view.title);
			}
		} catch (error) {
			log.warn(`Updating key ${action.id} failed: ${error instanceof Error ? error.message : String(error)}`);
		}
	}

	/** Sends a command; flashes the alert on the key if Apple Music could not do it. */
	protected async send(action: KeyAction<JsonObject>, command: CommandName): Promise<void> {
		const result = await this.music.command(command);
		if (!result.ok) {
			log.info(`"${command}" did not work: ${result.error ?? "unknown error"}.`);
			await action.showAlert();
		}
	}
}
