#!/usr/bin/env node
/**
 * Builds AppleMusicBridge.exe (self-contained, win-x64) and copies it next to plugin.js
 * (com.panix8th.applemusic.sdPlugin/bin/AppleMusicBridge.exe). Needs the .NET 8 SDK.
 */
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const pluginDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoDir = resolve(pluginDir, "..");
const project = join(repoDir, "helper", "AppleMusicBridge", "AppleMusicBridge.csproj");
const publishDir = join(repoDir, "helper", "publish");
const target = join(pluginDir, "com.panix8th.applemusic.sdPlugin", "bin", "AppleMusicBridge.exe");
const shown = (path) => relative(process.cwd(), path) || path;

try {
	execFileSync("dotnet", ["publish", project, "-c", "Release", "-o", publishDir, "--nologo"], { stdio: "inherit" });
} catch (error) {
	if (error.code === "ENOENT") {
		if (existsSync(target)) {
			console.warn(`dotnet was not found; keeping the existing ${shown(target)}.`);
			process.exit(0);
		}

		console.error(
			"The .NET 8 SDK (dotnet) is needed to build AppleMusicBridge.exe: https://dotnet.microsoft.com/download/dotnet/8.0\n" +
				`Alternatively download AppleMusicBridge.exe from the GitHub release and save it as ${shown(target)}.`,
		);
		process.exit(1);
	}

	process.exit(typeof error.status === "number" ? error.status : 1);
}

mkdirSync(dirname(target), { recursive: true });
try {
	copyFileSync(join(publishDir, "AppleMusicBridge.exe"), target);
} catch (error) {
	if (error.code === "EBUSY" || error.code === "EPERM") {
		console.error(
			`${shown(target)} is in use by the running plugin. Stop it first, then build again:\n` +
				"  streamdeck stop com.panix8th.applemusic",
		);
		process.exit(1);
	}

	throw error;
}

console.log(`AppleMusicBridge.exe -> ${shown(target)}`);
