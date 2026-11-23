import type { Log } from "../src/log";

/** A Log that records "level: message" lines. */
export function recordingLog(): Log & { lines: string[] } {
	const lines: string[] = [];
	const write =
		(level: string) =>
		(...data: unknown[]) =>
			lines.push(`${level}: ${data.map(String).join(" ")}`);

	return {
		lines,
		trace: write("trace"),
		debug: write("debug"),
		info: write("info"),
		warn: write("warn"),
		error: write("error"),
	};
}
