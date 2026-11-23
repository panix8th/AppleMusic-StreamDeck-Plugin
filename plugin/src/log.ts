/**
 * The part of the Stream Deck SDK logger this plugin uses. Modules take a `Log` instead of importing the
 * SDK, which keeps them testable without a Stream Deck connection.
 */
export type Log = {
	trace(...data: unknown[]): unknown;
	debug(...data: unknown[]): unknown;
	info(...data: unknown[]): unknown;
	warn(...data: unknown[]): unknown;
	error(...data: unknown[]): unknown;
};
