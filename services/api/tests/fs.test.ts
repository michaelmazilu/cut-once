import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { appendLine } from "../src/store/fs.js";

describe("durable line appends", () => {
  const dirs: string[] = [];
  afterEach(() => dirs.splice(0).forEach((dir) => rmSync(dir, { recursive: true, force: true })));

  it("appends and fsyncs through a writable handle", () => {
    const dir = mkdtempSync(join(tmpdir(), "cutonce-fsync-"));
    dirs.push(dir);
    const path = join(dir, "nested", "events.jsonl");
    appendLine(path, "one");
    appendLine(path, "two");
    expect(readFileSync(path, "utf8")).toBe("one\ntwo\n");
  });
});
