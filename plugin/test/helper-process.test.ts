import { once } from "node:events";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it } from "vitest";

import { HelperProcess } from "../src/bridge/helper-process";
import type { HelperMessage } from "../src/bridge/protocol";
import { recordingLog } from "./recording-log";

const FAKE_HELPER = fileURLToPath(new URL("./fixtures/fake-helper.mjs", import.meta.url));

const created: HelperProcess[] = [];

function createHelper(mode = "normal", command = process.execPath) {
	const log = recordingLog();
	const helperLog = recordingLog();
	const helper = new HelperProcess({
		command,
		args: [FAKE_HELPER, mode],
		log,
		helperLog,
		restartDelays: [50, 100],
		stableAfterMs: 60_000,
		stopTimeoutMs: 1_000,
	});
	const messages: HelperMessage[] = [];
	helper.on("message", (message) => messages.push(message));
	created.push(helper);
	return { helper, log, helperLog, messages };
}

async function waitFor(condition: () => boolean, timeoutMs = 5_000): Promise<void> {
	const start = Date.now();
	while (!condition()) {
		if (Date.now() - start > timeoutMs) {
			throw new Error("timed out waiting for condition");
		}

		await new Promise((resolve) => setTimeout(resolve, 10));
	}
}

afterEach(async () => {
	await Promise.all(created.splice(0).map((helper) => helper.stop()));
});

describe("HelperProcess", () => {
	it("parses protocol lines and skips anything else", async () => {
		const { helper, log, messages } = createHelper();
		helper.start();

		await waitFor(() => messages.length >= 2);
		expect(messages[0]).toMatchObject({ type: "hello", version: "9.9.9" });
		expect(messages[1]).toMatchObject({ type: "state", title: "Song", albumArtId: "art1" });

		await waitFor(() => log.lines.some((line) => line.includes("this is not json")));
		expect(messages.every((message) => ["hello", "state", "result"].includes(message.type))).toBe(true);
	});

	it("forwards stderr into the helper log with matching levels", async () => {
		const { helper, helperLog } = createHelper();
		helper.start();

		await waitFor(() => helperLog.lines.length >= 4);
		expect(helperLog.lines).toEqual(
			expect.arrayContaining([
				"info: fake helper started",
				"debug: debug line",
				"warn: warning line",
				"info: unprefixed line",
			]),
		);
	});

	it("sends commands and receives results", async () => {
		const { helper, messages } = createHelper();
		helper.start();
		await once(helper, "started");

		expect(helper.send({ id: 7, cmd: "ping" })).toBe(true);
		await waitFor(() => messages.some((message) => message.type === "result"));
		expect(messages.find((message) => message.type === "result")).toEqual({
			type: "result",
			id: 7,
			cmd: "ping",
			ok: true,
		});
	});

	it("restarts the helper after a crash", async () => {
		const { helper, log, helperLog, messages } = createHelper();
		let starts = 0;
		helper.on("started", () => starts++);
		helper.start();
		await waitFor(() => starts === 1);

		// "crash" is not a real command; the fake helper exits with code 1 on it.
		helper.send({ id: 1, cmd: "crash" as never });
		await waitFor(() => starts === 2);

		expect(log.lines.some((line) => line.startsWith("error: Helper exited unexpectedly (code 1"))).toBe(true);
		expect(log.lines.some((line) => line.includes("Last output:") && line.includes("crashing on purpose"))).toBe(true);
		expect(helperLog.lines).toContain("error: crashing on purpose");
		await waitFor(() => messages.filter((message) => message.type === "hello").length === 2);
		expect(helper.running).toBe(true);
	});

	it("keeps retrying with growing delays when the helper cannot start", async () => {
		const { helper, log } = createHelper("exit-immediately");
		helper.start();

		await waitFor(() => log.lines.filter((line) => line.startsWith("info: Restarting")).length >= 3);
		const delays = log.lines.filter((line) => line.startsWith("info: Restarting"));
		expect(delays[0]).toContain("0.05 s (attempt 1)");
		expect(delays[1]).toContain("0.1 s (attempt 2)");
		expect(delays[2]).toContain("0.1 s (attempt 3)");
	});

	it("reports a missing executable once and keeps retrying", async () => {
		const { helper, log } = createHelper("normal", "/definitely/not/AppleMusicBridge.exe");
		helper.start();

		await waitFor(() => log.lines.filter((line) => line.startsWith("info: Restarting")).length >= 3);
		const missing = log.lines.filter((line) => line.includes("AppleMusicBridge.exe not found"));
		expect(missing).toHaveLength(1);
		expect(helper.send({ id: 1, cmd: "ping" })).toBe(false);
	});

	it("stops by closing stdin and does not restart afterwards", async () => {
		const { helper, helperLog, log } = createHelper();
		let starts = 0;
		helper.on("started", () => starts++);
		helper.start();
		await waitFor(() => starts === 1);

		await helper.stop();
		expect(helper.running).toBe(false);
		await waitFor(() => helperLog.lines.includes("info: stdin closed"));

		await new Promise((resolve) => setTimeout(resolve, 300));
		expect(starts).toBe(1);
		expect(log.lines.some((line) => line.startsWith("info: Helper stopped"))).toBe(true);
		expect(log.lines.some((line) => line.startsWith("info: Restarting"))).toBe(false);
	});

	it("kill() ends the helper immediately", async () => {
		const { helper } = createHelper();
		helper.start();
		await once(helper, "started");

		const exited = once(helper, "exited");
		helper.kill();
		await exited;
		expect(helper.running).toBe(false);
	});
});
