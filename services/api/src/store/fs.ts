import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, writeFileSync, writeSync } from "node:fs";
import { dirname } from "node:path";

export const ensureDir = (dir: string) => mkdirSync(dir, { recursive: true });

/** Write to a temp file, then rename: a crash never leaves a half-written JSON file. */
export function writeJsonAtomic(path: string, value: unknown) {
  ensureDir(dirname(path));
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, JSON.stringify(value, null, 2) + "\n");
  renameSync(tmp, path);
}

export const readJson = <T>(path: string): T | null => (existsSync(path) ? (JSON.parse(readFileSync(path, "utf8")) as T) : null);

/** Append one line and fsync it: an acknowledged event survives a power cut. */
export function appendLine(path: string, line: string) {
  ensureDir(dirname(path));
  // Flush the same writable handle used for the append. Windows' FlushFileBuffers rejects the read-only handle
  // used here previously with EPERM, after the bytes had already been appended; that made starting a build fail
  // halfway through persistence even though the event file existed.
  const fd = openSync(path, "a");
  try {
    writeSync(fd, line + "\n");
    fsyncSync(fd);
  } finally {
    closeSync(fd);
  }
}

export const readLines = (path: string): string[] => (existsSync(path) ? readFileSync(path, "utf8").split("\n").filter(Boolean) : []);
