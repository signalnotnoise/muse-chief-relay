#!/usr/bin/env node
// Fail when the public tree or a Pages bundle contains a non-placeholder
// wss host, an obfuscated wss URL, a room-like secret, a build-time relay
// or room injection, a private Appwrite endpoint or project id, or a
// webhook URL. Findings name the file and the kind only. Values are not printed.
//
// Allowlist for hosts: example.com (and subdomains), localhost, 127.0.0.1,
// ::1, the RFC 2606 placeholders example.org and example.net, the fixture
// names example.test and example.invalid, the vendor docs host appwrite.io,
// and hack.chat (the public historical host the bridge still special-cases).
// A private relay is not on this list.

import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");

const SKIP_DIRS = new Set([".git", "node_modules", "bin", "obj"]);
const SKIP_FILES = new Set(["package-lock.json"]);
const TEXT_EXT = new Set([
  ".js", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".css", ".html", ".md",
  ".json", ".yml", ".yaml", ".txt", ".cs", ".swift", ".py", ".sh",
  ".example", ".env", ".xcconfig", ".plist", ".sln", ".xml",
]);

const WSS_HOST = /wss:\/(?:\$\(\))?\/+([^/\s"'`<>\\)\]]+)/gi;
const ROOM_ASSIGN = /["']?(?:channel|room|watch[_-]?channel)["']?\s*[:=]\s*["']([^"'\n]{16,})["']/gi;
const BUILD_INJECTION = /secrets\.VITE_(?:RELAY_URL|WATCH_CHANNEL)|VITE_(?:RELAY_URL|WATCH_CHANNEL):\s*\$\{\{\s*secrets\./g;
const APPWRITE_PROJECT = /APPWRITE_PROJECT_ID[^\n]{0,80}?([a-f0-9]{20,})/gi;
const APPWRITE_URL = /https?:\/\/([a-z0-9.-]*appwrite[a-z0-9.-]*)/gi;
const WEBHOOK_URL = /https?:\/\/(?:hooks\.slack\.com|discord(?:app)?\.com\/api\/webhooks)\//gi;
const OBFUSCATED = /wss:\/\$\(\)\//g;

const ROOM_HINT = /your-|placeholder|example|fixture|throwaway|sample|dummy|demo|test|lobby/;

function isAllowedHost(host) {
  const h = String(host || "").toLowerCase().replace(/\.$/, "");
  if (!h) return false;
  if (h === "localhost" || h === "127.0.0.1" || h === "::1" || h === "[::1]") return true;
  if (h.endsWith(".localhost")) return true;
  // example.com and the other reserved placeholder names, plus fixture TLDs.
  const suffixes = [
    "example.com",
    "example.org",
    "example.net",
    "example.test",
    "example.invalid",
    "example",
    "test",
    "invalid",
    "hack.chat",
    "appwrite.io",
  ];
  return suffixes.some((suffix) => h === suffix || h.endsWith("." + suffix));
}

function hostOf(authority) {
  const raw = String(authority || "").replace(/^\[/, "").replace(/\].*$/, "");
  const at = raw.lastIndexOf("@");
  const hostport = at >= 0 ? raw.slice(at + 1) : raw;
  if (hostport.startsWith("[")) return hostport.slice(1);
  return hostport.split(":")[0].replace(/[.,;)]+$/, "");
}

function isRoomLikeSecret(value) {
  const v = String(value || "").trim();
  if (v.length < 20 || !/^[A-Za-z0-9_-]+$/.test(v)) return false;
  if (ROOM_HINT.test(v.toLowerCase())) return false;
  const hex = /^[a-f0-9]{32,}$/i.test(v);
  const mixed = /[0-9]/.test(v) && /[A-Za-z]/.test(v);
  return hex || mixed;
}

function findingsIn(text) {
  const kinds = new Set();
  let match;
  WSS_HOST.lastIndex = 0;
  while ((match = WSS_HOST.exec(text))) {
    const host = hostOf(match[1]);
    if (!host || isAllowedHost(host)) continue;
    if (!/[a-z]/i.test(host)) continue;
    kinds.add("non-placeholder wss host");
  }
  if (OBFUSCATED.test(text)) kinds.add("obfuscated wss host");
  OBFUSCATED.lastIndex = 0;
  ROOM_ASSIGN.lastIndex = 0;
  while ((match = ROOM_ASSIGN.exec(text))) {
    if (isRoomLikeSecret(match[1])) kinds.add("room-like secret");
  }
  if (BUILD_INJECTION.test(text)) kinds.add("build-time relay or room injection");
  BUILD_INJECTION.lastIndex = 0;
  APPWRITE_PROJECT.lastIndex = 0;
  if (APPWRITE_PROJECT.test(text)) kinds.add("appwrite project id");
  APPWRITE_URL.lastIndex = 0;
  while ((match = APPWRITE_URL.exec(text))) {
    if (!isAllowedHost(match[1])) kinds.add("appwrite endpoint");
  }
  if (WEBHOOK_URL.test(text)) kinds.add("webhook url");
  WEBHOOK_URL.lastIndex = 0;
  return [...kinds];
}

function looksText(file) {
  const base = path.basename(file);
  if (SKIP_FILES.has(base)) return false;
  if (base.startsWith(".env") || base.endsWith(".example")) return true;
  const ext = path.extname(base).toLowerCase();
  if (ext && !TEXT_EXT.has(ext)) return false;
  return true;
}

function* walk(dir) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    if (SKIP_DIRS.has(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full);
    else if (entry.isFile() && looksText(full)) yield full;
  }
}

function trackedFiles() {
  const out = execFileSync("git", ["ls-files", "-z"], { cwd: ROOT, encoding: "utf8" });
  return out.split("\0").filter(Boolean).map((rel) => path.join(ROOT, rel)).filter(looksText);
}

function scanFile(file, rel) {
  let buf;
  try {
    buf = fs.readFileSync(file);
  } catch {
    return [];
  }
  if (buf.includes(0)) return [];
  const text = buf.toString("utf8");
  return findingsIn(text).map((kind) => ({ file: rel, kind }));
}

function relOf(file) {
  const rel = path.relative(ROOT, file);
  return rel.startsWith("..") ? file : rel.split(path.sep).join("/");
}

const extra = process.argv.slice(2);
const files = new Map();
for (const file of trackedFiles()) files.set(path.resolve(file), file);
for (const dir of extra) {
  const abs = path.resolve(dir);
  if (!fs.existsSync(abs)) {
    console.error("public leak guard: missing path " + dir);
    process.exit(2);
  }
  const stat = fs.statSync(abs);
  const list = stat.isDirectory() ? walk(abs) : [abs];
  for (const file of list) files.set(path.resolve(file), file);
}

const hits = [];
for (const file of files.values()) {
  hits.push(...scanFile(file, relOf(file)));
}

if (hits.length) {
  const seen = new Set();
  for (const hit of hits) {
    const line = hit.file + ": " + hit.kind;
    if (seen.has(line)) continue;
    seen.add(line);
    console.error(line);
  }
  console.error("public leak guard: " + seen.size + " finding(s)");
  process.exit(1);
}

console.log("public leak guard: ok");
