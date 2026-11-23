import { type ChildProcess, spawn } from "node:child_process";
import { EventEmitter } from "node:events";
import { createInterface } from "node:readline";

import type { Log } from "../log";
import { type Command, type HelperMessage, parseHelperMessage } from "./protocol";

export type HelperProcessOptions = {
	/** Executable to start (AppleMusicBridge.exe). */
	command: string;
	/** Command-line arguments. */
	args?: string[];
	/** Log for starting, stopping and restarting the helper. */
	log: Log;
	/** Log that receives the helper's own stderr lines. */
	helperLog: Log;
	/** Waits before successive restarts, in milliseconds; the last value repeats. */
	restartDelays?: number[];
	/** After running this long, the next crash counts as the first one again. */
	stableAfterMs?: number;
	/** How long stop() waits for the helper to exit by itself (stdin closed) before killing it. */
	stopTimeoutMs?: number;
};

type HelperProcessEvents = {
	message: [message: HelperMessage];
	started: [];
	exited: [code: number | null, signal: NodeJS.Signals | null];
};

const DEFAULT_RESTART_DELAYS = [1_000, 2_000, 5_000, 10_000, 30_000];
const LEVEL_PATTERN = /^\[(DEBUG|INFO|WARN|ERROR)\]\s?(.*)$/;

/**
 * Runs AppleMusicBridge.exe: starts it once, turns its stdout into protocol messages, forwards its stderr into
 * the plugin log, restarts it with a growing delay when it crashes, and stops it with the plugin.
 */
export class HelperProcess extends EventEmitter<HelperProcessEvents> {
	readonly #options: HelperProcessOptions;
	#child: ChildProcess | undefined;
	#restartTimer: NodeJS.Timeout | undefined;
	#attempt = 0;
	#startedAt = 0;
	#stopped = true;
	#missingReported = false;
	#recentStderr: string[] = [];

	constructor(options: HelperProcessOptions) {
		super();
		this.#options = options;
	}

	/** True while a helper process is alive. */
	get running(): boolean {
		const child = this.#child;
		return child !== undefined && child.pid !== undefined && child.exitCode === null && child.signalCode === null;
	}

	start(): void {
		if (!this.#stopped) {
			return;
		}

		this.#stopped = false;
		this.#spawn();
	}

	/** Writes one command line to the helper. Returns false when no helper is running. */
	send(command: Command): boolean {
		const stdin = this.#child?.stdin;
		if (!this.running || !stdin?.writable) {
			return false;
		}

		stdin.write(`${JSON.stringify(command)}\n`);
		return true;
	}

	/** Stops the helper for good: closes its stdin (it exits by itself), killing it if it does not. */
	async stop(): Promise<void> {
		this.#stopped = true;
		clearTimeout(this.#restartTimer);
		this.#restartTimer = undefined;

		const child = this.#child;
		if (!child || child.exitCode !== null || child.signalCode !== null) {
			return;
		}

		await new Promise<void>((resolve) => {
			const timer = setTimeout(() => child.kill(), this.#options.stopTimeoutMs ?? 2_000);
			child.once("exit", () => {
				clearTimeout(timer);
				resolve();
			});
			child.stdin?.end();
		});
	}

	/** Immediate, synchronous stop; for process exit handlers. */
	kill(): void {
		this.#stopped = true;
		clearTimeout(this.#restartTimer);
		this.#restartTimer = undefined;
		this.#child?.kill();
	}

	#spawn(): void {
		const { command, args = [], log } = this.#options;
		log.debug(`Starting ${command} ${args.join(" ")}`.trim());

		let child: ChildProcess;
		try {
			child = spawn(command, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
		} catch (error) {
			this.#reportStartFailure(error);
			this.#scheduleRestart();
			return;
		}

		this.#child = child;
		this.#startedAt = Date.now();
		this.#recentStderr = [];

		child.once("spawn", () => {
			this.#missingReported = false;
			log.info(`Helper started (pid ${child.pid}).`);
			this.emit("started");
		});
		child.once("error", (error) => this.#onExit(child, null, null, error));
		// "close" rather than "exit": it fires once stdout/stderr are drained, so the last output is complete.
		child.once("close", (code, signal) => this.#onExit(child, code, signal));

		// Writing to a helper that just died raises EPIPE on stdin; the exit handler takes care of it.
		child.stdin?.on("error", (error) => log.debug(`Helper stdin: ${error.message}`));

		if (child.stdout) {
			createInterface({ input: child.stdout }).on("line", (line) => this.#onStdout(child, line));
		}

		if (child.stderr) {
			createInterface({ input: child.stderr }).on("line", (line) => this.#onStderr(line));
		}
	}

	#onStdout(child: ChildProcess, line: string): void {
		if (child !== this.#child || line.trim().length === 0) {
			return;
		}

		const message = parseHelperMessage(line);
		if (message) {
			this.emit("message", message);
		} else {
			this.#options.log.warn(`Ignoring unexpected helper output: ${line.length > 200 ? `${line.slice(0, 200)}...` : line}`);
		}
	}

	#onStderr(line: string): void {
		this.#recentStderr.push(line);
		if (this.#recentStderr.length > 10) {
			this.#recentStderr.shift();
		}

		const log = this.#options.helperLog;
		const match = LEVEL_PATTERN.exec(line);
		if (!match) {
			log.info(line);
			return;
		}

		const [, level, text] = match;
		switch (level) {
			case "DEBUG":
				log.debug(text);
				break;
			case "WARN":
				log.warn(text);
				break;
			case "ERROR":
				log.error(text);
				break;
			default:
				log.info(text);
		}
	}

	#onExit(child: ChildProcess, code: number | null, signal: NodeJS.Signals | null, error?: Error): void {
		// "error" and "exit" can both fire for the same process; handle it once.
		if (child !== this.#child) {
			return;
		}

		this.#child = undefined;
		const { log } = this.#options;

		if (error && child.pid === undefined) {
			this.#reportStartFailure(error);
		} else if (this.#stopped) {
			log.info(`Helper stopped (code ${code ?? "-"}).`);
		} else {
			const lastLines = this.#recentStderr.length > 0 ? ` Last output: ${this.#recentStderr.join(" | ")}` : "";
			log.error(`Helper exited unexpectedly (code ${code ?? "-"}, signal ${signal ?? "-"}).${lastLines}`);
		}

		this.emit("exited", code, signal);

		if (!this.#stopped) {
			this.#scheduleRestart();
		}
	}

	#reportStartFailure(error: unknown): void {
		const { command, log } = this.#options;
		const code = (error as NodeJS.ErrnoException | undefined)?.code;
		if (code === "ENOENT") {
			if (!this.#missingReported) {
				this.#missingReported = true;
				log.error(
					`AppleMusicBridge.exe not found at ${command}. Build it with "npm run build:helper", or download it ` +
						`from the GitHub release and put it into the plugin's bin folder.`,
				);
			}

			return;
		}

		log.error(`Could not start the helper: ${error instanceof Error ? error.message : String(error)}`);
	}

	#scheduleRestart(): void {
		if (this.#stopped || this.#restartTimer) {
			return;
		}

		const delays = this.#options.restartDelays ?? DEFAULT_RESTART_DELAYS;
		if (Date.now() - this.#startedAt >= (this.#options.stableAfterMs ?? 60_000)) {
			this.#attempt = 0;
		}

		const delay = delays[Math.min(this.#attempt, delays.length - 1)];
		this.#attempt++;
		this.#options.log.info(`Restarting the helper in ${delay / 1000} s (attempt ${this.#attempt}).`);

		this.#restartTimer = setTimeout(() => {
			this.#restartTimer = undefined;
			if (!this.#stopped) {
				this.#spawn();
			}
		}, delay);
	}
}
