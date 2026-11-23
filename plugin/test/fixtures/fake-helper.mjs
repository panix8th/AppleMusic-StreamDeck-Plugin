// Stands in for AppleMusicBridge.exe in tests: speaks the same protocol over stdin/stdout/stderr.
//   node fake-helper.mjs [normal|exit-immediately]
// Commands: any known command answers ok; "fail" answers an error; "silent" never answers;
// "crash" exits with code 1. Closing stdin exits with code 0, like the real helper.
import { createInterface } from "node:readline";

const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`);

if (process.argv[2] === "exit-immediately") {
	console.error("[ERROR] cannot start");
	process.exit(3);
}

console.error("[INFO] fake helper started");
console.error("[DEBUG] debug line");
console.error("[WARN] warning line");
console.error("unprefixed line");
send({ type: "hello", version: "9.9.9", pid: process.pid, debug: false });
send({
	type: "state",
	appRunning: true,
	hasSession: true,
	title: "Song",
	artist: "Artist",
	album: "Album",
	isPlaying: true,
	status: "playing",
	shuffle: null,
	repeat: null,
	position: 1,
	duration: 100,
	timestamp: Date.now(),
	albumArtId: "art1",
	albumArt: "iVBORw0KGgo=",
	supports: { play: false, pause: true, playPause: true, next: true, previous: true, shuffle: false, repeat: false },
	sourceAppId: "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
});
process.stdout.write("this is not json\n");

createInterface({ input: process.stdin })
	.on("line", (line) => {
		const command = JSON.parse(line);
		switch (command.cmd) {
			case "crash":
				console.error("[ERROR] crashing on purpose");
				process.exit(1);
				break;
			case "fail":
				send({ type: "result", id: command.id, cmd: command.cmd, ok: false, error: "rejected" });
				break;
			case "silent":
				break;
			default:
				send({ type: "result", id: command.id, cmd: command.cmd, ok: true });
		}
	})
	.on("close", () => {
		console.error("[INFO] stdin closed");
		process.exit(0);
	});
