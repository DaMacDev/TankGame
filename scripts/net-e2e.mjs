#!/usr/bin/env node
// Real-socket multiplayer check: a local worker plus N headless game processes that create, join
// and start a match through the real scenes, then report whether every tank spawned and moved.
// Usage: node scripts/net-e2e.mjs [humans=3] [mapId=random]   (env GODOT = engine binary, default "godot";
// env NET_E2E_SHOT = path prefix: runs the host in a window and saves a screenshot of the match)
import { spawn, execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const humans = Number(process.argv[2] ?? 3);
const map = process.argv[3];
const godot = process.env.GODOT ?? "godot";
const shot = process.env.NET_E2E_SHOT;
const port = 8787 + Math.floor(Math.random() * 1000);
const worker = `http://127.0.0.1:${port}`;
const work = mkdtempSync(path.join(tmpdir(), "net-e2e-"));
const isWindows = process.platform === "win32";

function killTree(child) {
  if (child.exitCode !== null) return;
  if (isWindows) {
    execFileSync("taskkill", ["/T", "/F", "/PID", String(child.pid)], { stdio: "ignore" });
  } else {
    process.kill(-child.pid, "SIGKILL"); // detached below, so this is our own process group
  }
}

async function waitForWorker(child) {
  for (let i = 0; i < 120; i++) {
    if (child.exitCode !== null) throw new Error(`worker exited with ${child.exitCode}`);
    try {
      if ((await fetch(`${worker}/healthz`)).ok) return;
    } catch {
      // not listening yet
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error("worker did not come up");
}

function runPlayer(role, index) {
  const out = path.join(work, `${role}-${index}.json`);
  const log = [];
  const child = spawn(
    godot,
    [
      ...(shot && role === "host" ? [] : ["--headless"]), "--path", path.join(root, "client"), "--",
      ...(shot && role === "host" ? [`--net-e2e-shot=${shot}`] : []),
      ...(map ? [`--net-e2e-map=${map}`] : []),
      `--net-e2e-role=${role}`, `--net-e2e-worker=${worker}`, `--net-e2e-humans=${humans}`,
      `--net-e2e-code-file=${path.join(work, "code.txt")}`, `--net-e2e-out=${out}`,
    ],
    { stdio: ["ignore", "pipe", "pipe"], shell: isWindows },
  );
  for (const stream of [child.stdout, child.stderr]) stream.on("data", (chunk) => log.push(String(chunk)));
  return new Promise((resolve) => {
    child.on("exit", () => {
      const text = log.join("");
      const report = existsSync(out)
        ? JSON.parse(readFileSync(out, "utf8"))
        : { role, passed: false, detail: "no report" };
      if (/(ObjectDisposedException|Unhandled exception)/.test(text)) {
        report.passed = false;
        report.detail = `${report.detail}; exception: ${/.*(ObjectDisposedException|Unhandled exception).*/.exec(text)[0].trim()}`;
      }
      if (!report.passed) report.detail += `\n${text.split("\n").slice(-12).join("\n")}`; // for the CI log
      resolve(report);
    });
  });
}

const wrangler = spawn(
  isWindows ? "npx.cmd" : "npx",
  ["wrangler", "dev", "--local", "--port", String(port), "--ip", "127.0.0.1"],
  { cwd: path.join(root, "server", "worker"), stdio: "ignore", shell: isWindows, detached: !isWindows },
);

let failed = true;
try {
  await waitForWorker(wrangler);
  const roles = ["host", ...Array.from({ length: humans - 1 }, () => "guest")];
  const reports = await Promise.all(roles.map(runPlayer));
  for (const report of reports) {
    console.log(`${report.passed ? "PASS" : "FAIL"} ${report.role} slot=${report.slot ?? "?"} tanks=${report.tanksSeen ?? "?"} ${report.detail}`);
  }
  failed = reports.some((report) => !report.passed);
} finally {
  killTree(wrangler);
  rmSync(work, { recursive: true, force: true });
}
process.exit(failed ? 1 : 0);
