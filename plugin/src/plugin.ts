import streamDeck from "@elgato/streamdeck";
import { fileURLToPath } from "node:url";

import { NowPlayingAction } from "./actions/now-playing";
import { PlayPauseAction } from "./actions/play-pause";
import { RepeatAction, ShuffleAction } from "./actions/shuffle-repeat";
import { NextTrackAction, PreviousTrackAction } from "./actions/skip";
import { AppleMusic } from "./bridge/apple-music";
import { HelperProcess } from "./bridge/helper-process";

/**
 * Set to true to start the helper with --debug: it then logs every media event and raw field into this
 * plugin's log (useful for diagnosing, very chatty).
 */
const HELPER_DEBUG = false;

// "trace" would also log every message exchanged with Stream Deck, including each cover image.
streamDeck.logger.setLevel("debug");

const helper = new HelperProcess({
	// AppleMusicBridge.exe sits next to this file (bin/plugin.js).
	command: fileURLToPath(new URL("AppleMusicBridge.exe", import.meta.url)),
	// --parent-pid: the helper exits by itself as soon as this process is gone, however it ends.
	args: ["--parent-pid", String(process.pid), ...(HELPER_DEBUG ? ["--debug"] : [])],
	log: streamDeck.logger.createScope("Bridge"),
	helperLog: streamDeck.logger.createScope("Helper"),
});

const music = new AppleMusic(helper, streamDeck.logger.createScope("AppleMusic"));

streamDeck.actions.registerAction(new NowPlayingAction(music));
streamDeck.actions.registerAction(new PlayPauseAction(music));
streamDeck.actions.registerAction(new NextTrackAction(music));
streamDeck.actions.registerAction(new PreviousTrackAction(music));
streamDeck.actions.registerAction(new ShuffleAction(music));
streamDeck.actions.registerAction(new RepeatAction(music));

// Stream Deck tells us when AppleMusic.exe (ApplicationsToMonitor in manifest.json) starts or exits; the
// helper then re-checks right away instead of waiting for Apple Music to (un)register its media session.
streamDeck.system.onApplicationDidLaunch((ev) => {
	streamDeck.logger.info(`${ev.application} started.`);
	music.refresh();
});
streamDeck.system.onApplicationDidTerminate((ev) => {
	streamDeck.logger.info(`${ev.application} exited.`);
	music.refresh();
});
streamDeck.system.onSystemDidWakeUp(() => music.refresh());

// Stop the helper together with the plugin. (If the plugin is killed outright, the helper notices that its
// stdin closed and that its parent is gone, and exits by itself.)
process.once("exit", () => helper.kill());
for (const signal of ["SIGINT", "SIGTERM"] as const) {
	process.once(signal, () => {
		helper.kill();
		process.exit(0);
	});
}

helper.start();
streamDeck.connect();
