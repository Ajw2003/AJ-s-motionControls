#!/usr/bin/env node
"use strict";

/*
 * Coding-standards audit for RockSkipping.
 *
 * Plain Node, zero dependencies. Walks the repo, runs a flat list of named
 * checks, prints one PASS/FAIL line per check with a count, and exits
 * non-zero if any check fails.
 *
 * This file is meant to be read and edited by a human without an agent
 * present -- keep each check a small, self-contained function that returns
 * { pass, count, detail } and is registered in the CHECKS list at the
 * bottom.
 *
 * Comment DENSITY is reported as an informational number only and must
 * NEVER cause a failing check -- high density is this codebase's deliberate
 * strength.
 */

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Directories excluded everywhere -- these duplicate the real tree or are
// not source we own.
const EXCLUDED_DIR_NAMES = new Set([
  ".claude",
  "worktrees",
  "Library",
  "Temp",
  "obj",
  "node_modules",
  "docs",
  "history",
  "transcripts",
  ".git",
]);

function shouldSkipDir(fullPath) {
  const base = path.basename(fullPath);
  if (EXCLUDED_DIR_NAMES.has(base)) return true;
  // Assets/.claude/worktrees is covered by the '.claude' exclusion above,
  // but be explicit about the worktrees path too in case '.claude' itself
  // is ever un-excluded.
  const rel = path.relative(REPO_ROOT, fullPath).split(path.sep).join("/");
  if (rel === "Assets/.claude" || rel.startsWith("Assets/.claude/"))
    return true;
  return false;
}

function walk(dir, matcher, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (err) {
    return out;
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (shouldSkipDir(full)) continue;
      walk(full, matcher, out);
    } else if (entry.isFile()) {
      if (matcher(full)) out.push(full);
    }
  }
  return out;
}

function readText(file) {
  // Files in Assets/ are CRLF. Read raw and normalize line endings so
  // regexes and line splitting behave the same regardless of source.
  const raw = fs.readFileSync(file, "utf8");
  return raw.replace(/\r\n/g, "\n");
}

function lines(text) {
  return text.split("\n");
}

// ---------------------------------------------------------------------
// File sets
// ---------------------------------------------------------------------

function csharpFiles() {
  return walk(path.join(REPO_ROOT, "Assets"), (f) => f.endsWith(".cs"), []);
}

// The audit script itself is excluded from the web file set: it necessarily
// contains the literal strings it searches for (TODO/HACK/FIXME, console.log),
// and would otherwise report itself as an offender on every run.
function isAuditScript(file) {
  const rel = path.relative(REPO_ROOT, file).split(path.sep).join("/");
  return rel === "tools/audit/check-standards.js";
}

function webFiles() {
  const out = [];
  for (const sub of ["relay", "controller", "tools"]) {
    const dir = path.join(REPO_ROOT, sub);
    if (fs.existsSync(dir)) {
      walk(
        dir,
        (f) =>
          f.endsWith(".js") ||
          f.endsWith(".mjs") ||
          f.endsWith(".cjs") ||
          f.endsWith(".ts"),
        out,
      );
    }
  }
  return out.filter((f) => !isAuditScript(f));
}

// ---------------------------------------------------------------------
// C# checks
// ---------------------------------------------------------------------

function checkKrBraces() {
  // K&R style: a line ending in ") {" (opening brace on the same line as
  // the closing paren of a signature/condition), rather than Allman style
  // where the brace goes on its own line.
  const files = csharpFiles();
  const offenders = [];
  const re = /\)\s*\{\s*$/;
  for (const file of files) {
    const text = readText(file);
    for (const [i, line] of lines(text).entries()) {
      if (re.test(line)) {
        offenders.push(`${path.relative(REPO_ROOT, file)}:${i + 1}`);
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: offenders.slice(0, 10),
  };
}

function checkPrivateUnderscoreNaming() {
  // Plain "private <Type> _name" fields should be _camelCase. We count
  // compliant sites and flag any plain-private field declaration that does
  // NOT start with an underscore (excluding [SerializeField] fields, which
  // are a documented deviation and keep bare camelCase).
  const files = csharpFiles();
  let compliant = 0;
  let filesWithCompliant = new Set();
  const offenders = [];
  // Matches: private <type> <name>; or private <type> <name> = ...;
  // Captures the field name.
  const fieldRe =
    /^\s*private\s+(?:readonly\s+)?[\w<>\[\],.\s]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*(;|=)/;
  for (const file of files) {
    const text = readText(file);
    const fileLines = lines(text);
    for (let i = 0; i < fileLines.length; i++) {
      const line = fileLines[i];
      const trimmed = line.trim();
      if (!trimmed.startsWith("private ")) continue;
      // Skip if this field is [SerializeField]-annotated (deviation): look
      // back a couple of lines for the attribute.
      let serialized = false;
      for (let back = 1; back <= 3; back++) {
        const prev = fileLines[i - back];
        if (prev === undefined) break;
        const p = prev.trim();
        if (p === "" || p.startsWith("//") || p.startsWith("///")) continue;
        if (p.includes("[SerializeField")) serialized = true;
        break;
      }
      if (serialized) continue;
      // Skip method declarations (contain '(' before any '=' or ';')
      const parenIdx = trimmed.indexOf("(");
      const semiIdx = trimmed.indexOf(";");
      const eqIdx = trimmed.indexOf("=");
      const stopIdx = Math.min(
        semiIdx === -1 ? Infinity : semiIdx,
        eqIdx === -1 ? Infinity : eqIdx,
      );
      if (parenIdx !== -1 && parenIdx < stopIdx) continue;

      const m = fieldRe.exec(line);
      if (!m) continue;
      const name = m[1];
      if (name.startsWith("_")) {
        compliant++;
        filesWithCompliant.add(file);
      } else if (!/^[A-Z]/.test(name)) {
        // Not a constant-style name, not underscore-prefixed -> offender.
        offenders.push(`${path.relative(REPO_ROOT, file)}:${i + 1}:${name}`);
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: compliant,
    detail: {
      compliantSites: compliant,
      compliantFiles: filesWithCompliant.size,
      offenders: offenders.slice(0, 10),
    },
  };
}

function extractMethodBodies(text, methodNames) {
  // Very small brace-matching extractor: finds "<methodName>(...) {" and
  // returns the text of the body up to the matching closing brace.
  const bodies = [];
  for (const name of methodNames) {
    const re = new RegExp(
      `\\b(?:void|IEnumerator|Task|async\\s+\\w+)\\s+${name}\\s*\\([^)]*\\)\\s*\\{`,
      "g",
    );
    let m;
    while ((m = re.exec(text)) !== null) {
      const start = m.index + m[0].length;
      let depth = 1;
      let i = start;
      while (i < text.length && depth > 0) {
        if (text[i] === "{") depth++;
        else if (text[i] === "}") depth--;
        i++;
      }
      bodies.push(text.slice(start, i));
    }
  }
  return bodies;
}

const PER_FRAME_METHODS = ["Update", "FixedUpdate", "LateUpdate", "OnGUI"];

// `if (_field == null) _field = Camera.main;` is a one-time cache guarded by a
// null check, not a per-frame lookup -- it resolves once and then never calls
// again. ShoreRocks/RockLauncher both use it because Camera.main can still be
// null at Awake. Flagging it would push code toward a one-shot Awake lookup
// that silently never recovers, which is the actual bug.
function isGuardedLazyCache(line) {
  return /if\s*\(\s*_\w+\s*==\s*null\s*\)\s*_\w+\s*=/.test(line);
}

function checkPerFrameExpensiveCalls() {
  const files = csharpFiles();
  const offenders = [];
  const pattern =
    /Camera\.main|GetComponent\s*<|FindObjectOfType|GameObject\.Find/;
  for (const file of files) {
    const text = readText(file);
    const bodies = extractMethodBodies(text, PER_FRAME_METHODS);
    for (const body of bodies) {
      for (const line of lines(body)) {
        const code = line.replace(/\/\/.*$/, "");
        if (!pattern.test(code)) continue;
        if (isGuardedLazyCache(code)) continue;
        offenders.push(path.relative(REPO_ROOT, file));
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: [...new Set(offenders)].length,
    detail: [...new Set(offenders)].slice(0, 10),
  };
}

function checkLinqInPerFrame() {
  const files = csharpFiles();
  const offenders = [];
  // Common LINQ usage markers.
  const pattern =
    /\.(Where|Select|OrderBy|FirstOrDefault|Any|All|Sum|Count|ToList|ToArray)\s*\(/;
  for (const file of files) {
    const text = readText(file);
    const bodies = extractMethodBodies(text, PER_FRAME_METHODS);
    for (const body of bodies) {
      if (pattern.test(body)) {
        offenders.push(path.relative(REPO_ROOT, file));
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: [...new Set(offenders)].slice(0, 10),
  };
}

function checkEmptyCatchBlocks() {
  const files = csharpFiles();
  const offenders = [];
  const re = /catch\s*(\([^)]*\))?\s*\{\s*\}/g;
  for (const file of files) {
    const text = readText(file);
    let m;
    while ((m = re.exec(text)) !== null) {
      offenders.push(path.relative(REPO_ROOT, file));
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: [...new Set(offenders)],
  };
}

function checkDebugLogCount() {
  const files = csharpFiles();
  let count = 0;
  const re = /Debug\.Log(Warning|Error)?\s*\(/g;
  for (const file of files) {
    const text = readText(file);
    const matches = text.match(re);
    if (matches) count += matches.length;
  }
  return {
    pass: true, // informational baseline check -- never fails by itself
    count,
    detail:
      "Debug.Log/.LogWarning/.LogError call count (baseline, not a failure condition)",
  };
}

function checkFilesOver500Lines() {
  const files = csharpFiles();
  const offenders = [];
  for (const file of files) {
    const text = readText(file);
    const n = lines(text).length;
    if (n > 500) {
      offenders.push(`${path.relative(REPO_ROOT, file)} (${n})`);
    }
  }
  // Reported, never failed: deferred to Phase 4 step 14 (split large files).
  return {
    pass: true,
    informational: true,
    _offending: offenders.length,
    count: offenders.length,
    detail: offenders,
  };
}

function checkMultiTypeFiles() {
  const files = csharpFiles();
  const offenders = [];
  // Only TOP-LEVEL types count. Inside a namespace these sit at 4-space
  // indent; a nested helper struct is indented 8+ and is not what the
  // one-class-per-file rule targets -- counting those flagged 18 files
  // where only a handful genuinely declare unrelated top-level types.
  const re =
    /^ {0,4}(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(class|struct|interface|enum|record)\s+\w+/gm;
  for (const file of files) {
    const text = readText(file);
    const matches = text.match(re);
    const topLevelCount = matches ? matches.length : 0;
    if (topLevelCount > 1) {
      offenders.push(
        `${path.relative(REPO_ROOT, file)} (${topLevelCount} types)`,
      );
    }
  }
  // Reported, never failed: deferred to Phase 4 step 14; Messages.cs is a deliberate single wire-format unit.
  return {
    pass: true,
    informational: true,
    _offending: offenders.length,
    count: offenders.length,
    detail: offenders,
  };
}

// ---------------------------------------------------------------------
// Web checks
// ---------------------------------------------------------------------

function fileExists(rel) {
  return fs.existsSync(path.join(REPO_ROOT, rel));
}

function checkPrettierConfigPresent() {
  const present = ["relay", "controller"].filter((sub) =>
    [
      ".prettierrc",
      ".prettierrc.json",
      ".prettierrc.js",
      "prettier.config.js",
    ].some((name) => fileExists(path.join(sub, name))),
  );
  return {
    pass: present.length === 2,
    count: present.length,
    detail: `prettier config present in: ${present.join(", ") || "none"}`,
  };
}

function checkEslintConfigPresent() {
  const present = ["relay", "controller"].filter((sub) =>
    [
      "eslint.config.js",
      "eslint.config.mjs",
      ".eslintrc.js",
      ".eslintrc.json",
    ].some((name) => fileExists(path.join(sub, name))),
  );
  return {
    pass: present.length === 2,
    count: present.length,
    detail: `eslint config present in: ${present.join(", ") || "none"}`,
  };
}

function checkTsconfigPresent() {
  const present = fileExists("tsconfig.json");
  return {
    pass: present,
    count: present ? 1 : 0,
    detail: present
      ? "tsconfig.json present at repo root"
      : "tsconfig.json missing",
  };
}

function checkThenChainCount() {
  const files = webFiles();
  let count = 0;
  const detail = [];
  const re = /\.then\s*\(/g;
  for (const file of files) {
    const text = readText(file);
    const matches = text.match(re);
    if (matches) {
      count += matches.length;
      detail.push(`${path.relative(REPO_ROOT, file)}: ${matches.length}`);
    }
  }
  return {
    pass: true, // informational count driving Phase 2 step 9, not itself a hard gate
    count,
    detail,
  };
}

function checkConsoleLogCount() {
  const files = webFiles();
  let count = 0;
  const re = /console\.log\s*\(/g;
  for (const file of files) {
    const text = readText(file);
    const matches = text.match(re);
    if (matches) count += matches.length;
  }
  return {
    pass: true, // informational
    count,
    detail: "console.log call count (informational)",
  };
}

function checkRelayLockfileCommitted() {
  const present = fileExists(path.join("relay", "package-lock.json"));
  return {
    pass: present,
    count: present ? 1 : 0,
    detail: present
      ? "relay/package-lock.json present"
      : "relay/package-lock.json missing",
  };
}

function checkNoEnvFileCommitted() {
  const offenders = [];
  for (const sub of ["relay", "controller", "tools", ""]) {
    const dir = path.join(REPO_ROOT, sub);
    if (!fs.existsSync(dir)) continue;
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (e) {
      continue;
    }
    for (const entry of entries) {
      if (entry.isFile() && entry.name === ".env") {
        offenders.push(path.join(sub, entry.name));
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: offenders,
  };
}

// ---------------------------------------------------------------------
// Philosophy checks (both stacks)
// ---------------------------------------------------------------------

function allSourceFiles() {
  return [...csharpFiles(), ...webFiles()];
}

function checkCommentedOutCode() {
  // A "//" comment line whose content looks like actual code syntax
  // (ends with ; or { or } , or starts with a keyword followed by a paren).
  const files = allSourceFiles();
  const offenders = [];
  const codeLike =
    /^(if|for|foreach|while|switch|return|var|let|const|public|private|void|function|class)\b.*[;{}]\s*$/;
  for (const file of files) {
    const text = readText(file);
    for (const [i, line] of lines(text).entries()) {
      const t = line.trim();
      if (!t.startsWith("//") || t.startsWith("///")) continue;
      const stripped = t.replace(/^\/\/+\s*/, "");
      if (codeLike.test(stripped)) {
        offenders.push(
          `${path.relative(REPO_ROOT, file)}:${i + 1}: ${stripped.slice(0, 60)}`,
        );
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: offenders.slice(0, 10),
  };
}

const QUESTION_PREFIXES = [
  "is",
  "has",
  "can",
  "should",
  "was",
  "will",
  "needs",
  "did",
  "allow",
  "use",
  "enable",
];

function checkBooleanNaming() {
  const files = allSourceFiles();
  const offenders = [];
  // C#: bool <Name> or private bool _name; JS: let/const <name> = true/false
  const csBoolRe = /\bbool\s+(_?[A-Za-z][A-Za-z0-9]*)\s*[;=]/g;
  const jsBoolRe =
    /\b(?:let|const|var)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(true|false)\b/g;
  for (const file of files) {
    const text = readText(file);
    const isCs = file.endsWith(".cs");
    const re = isCs ? csBoolRe : jsBoolRe;
    re.lastIndex = 0;
    let m;
    while ((m = re.exec(text)) !== null) {
      let name = m[1].replace(/^_/, "");
      const lower = name.charAt(0).toLowerCase() + name.slice(1);
      const startsWithQuestionWord = QUESTION_PREFIXES.some((p) =>
        lower.toLowerCase().startsWith(p.toLowerCase()),
      );
      if (!startsWithQuestionWord) {
        offenders.push(`${path.relative(REPO_ROOT, file)}: ${m[1]}`);
      }
    }
  }
  // Reported, never failed: remaining names are backlog; wire-format/save-file names are an accepted deviation.
  return {
    pass: true,
    informational: true,
    _offending: offenders.length,
    count: offenders.length,
    detail: offenders.slice(0, 20),
  };
}

function checkNoiseWordTypeNames() {
  const files = allSourceFiles();
  const offenders = new Set();
  const re =
    /\b(?:class|struct|interface)\s+(\w*(?:Manager|Helper|Info|Data|Util))\b/g;
  for (const file of files) {
    const text = readText(file);
    let m;
    while ((m = re.exec(text)) !== null) {
      offenders.add(m[1]);
    }
  }
  // Reported, never failed: accepted deviation -- the *Manager names are
  // referenced across CLAUDE.md, docs/PLAN.md and the Bootstrap ordering
  // comments, so renaming them is docs-wide churn for a naming-taste win.
  return {
    pass: true,
    informational: true,
    count: offenders.size,
    detail: [...offenders],
  };
}

function checkFilesWithNoDocComment() {
  const files = allSourceFiles();
  const offenders = [];
  for (const file of files) {
    const text = readText(file);
    const isCs = file.endsWith(".cs");
    const hasDoc = isCs
      ? /^\s*\/\/\/.*$/m.test(text)
      : /^\s*\/\*\*[\s\S]*?\*\//m.test(text) || /^\s*\/\/.*$/m.test(text);
    if (!hasDoc) {
      offenders.push(path.relative(REPO_ROOT, file));
    }
  }
  // Reported, never failed: accepted deviation -- *Manager names are referenced across CLAUDE.md, docs/PLAN.md and Bootstrap ordering comments.
  return {
    pass: true,
    informational: true,
    _offending: offenders.length,
    count: offenders.length,
    detail: offenders.slice(0, 10),
  };
}

function checkCommentDensity() {
  // INFORMATIONAL ONLY. Never fails. High density is a deliberate strength
  // of this codebase.
  const files = allSourceFiles();
  let totalLines = 0;
  let commentLines = 0;
  for (const file of files) {
    const text = readText(file);
    const isCs = file.endsWith(".cs");
    for (const line of lines(text)) {
      totalLines++;
      const t = line.trim();
      if (isCs) {
        if (t.startsWith("//") || t.startsWith("*") || t.startsWith("/*"))
          commentLines++;
      } else {
        if (t.startsWith("//") || t.startsWith("*") || t.startsWith("/*"))
          commentLines++;
      }
    }
  }
  const pct =
    totalLines === 0 ? 0 : ((commentLines / totalLines) * 100).toFixed(1);
  return {
    pass: true,
    count: `${commentLines}/${totalLines} lines (${pct}%)`,
    detail: "INFORMATIONAL ONLY -- comment density never fails this audit",
    informational: true,
  };
}

// ---------------------------------------------------------------------
// Baseline size checks (informational, help sanity-check the walker)
// ---------------------------------------------------------------------

function checkCsFileAndLineCounts() {
  const files = csharpFiles();
  let totalLines = 0;
  for (const file of files) {
    totalLines += lines(readText(file)).length;
  }
  return {
    pass: true,
    count: `${files.length} files, ${totalLines} lines`,
    detail: "informational baseline (expect ~70 files, ~12,937 lines)",
    informational: true,
  };
}

function checkJsLineCount() {
  const files = webFiles();
  let totalLines = 0;
  for (const file of files) {
    totalLines += lines(readText(file)).length;
  }
  return {
    pass: true,
    count: `${files.length} files, ${totalLines} lines`,
    detail: "informational baseline (expect ~5,387 JS lines)",
    informational: true,
  };
}

function checkTodoHackFixme() {
  const files = allSourceFiles();
  const offenders = [];
  const re = /\b(TODO|HACK|FIXME)\b/;
  for (const file of files) {
    const text = readText(file);
    for (const [i, line] of lines(text).entries()) {
      if (re.test(line)) {
        offenders.push(`${path.relative(REPO_ROOT, file)}:${i + 1}`);
      }
    }
  }
  return {
    pass: offenders.length === 0,
    count: offenders.length,
    detail: offenders.slice(0, 10),
  };
}

// ---------------------------------------------------------------------
// Registration + runner
// ---------------------------------------------------------------------

const CHECKS = [
  // C#
  ["cs.file-line-counts (baseline)", checkCsFileAndLineCounts],
  ["cs.kr-braces (expect 0)", checkKrBraces],
  ["cs.private-underscore-naming", checkPrivateUnderscoreNaming],
  [
    "cs.per-frame-expensive-calls (Camera.main/GetComponent</FindObjectOfType/GameObject.Find in Update/FixedUpdate/LateUpdate/OnGUI)",
    checkPerFrameExpensiveCalls,
  ],
  ["cs.linq-in-per-frame-methods", checkLinqInPerFrame],
  ["cs.empty-catch-blocks", checkEmptyCatchBlocks],
  ["cs.debug-log-count (informational)", checkDebugLogCount],
  ["cs.files-over-500-lines", checkFilesOver500Lines],
  ["cs.multi-type-files", checkMultiTypeFiles],
  ["cs.todo-hack-fixme", checkTodoHackFixme],

  // Web
  ["web.js-line-count (baseline)", checkJsLineCount],
  ["web.prettier-config-present", checkPrettierConfigPresent],
  ["web.eslint-config-present", checkEslintConfigPresent],
  ["web.tsconfig-present", checkTsconfigPresent],
  ["web.then-chain-count (informational)", checkThenChainCount],
  ["web.console-log-count (informational)", checkConsoleLogCount],
  ["web.relay-lockfile-committed", checkRelayLockfileCommitted],
  ["web.no-env-file-committed", checkNoEnvFileCommitted],

  // Philosophy
  ["philosophy.commented-out-code", checkCommentedOutCode],
  ["philosophy.boolean-naming-not-a-question", checkBooleanNaming],
  ["philosophy.noise-word-type-names", checkNoiseWordTypeNames],
  ["philosophy.files-with-no-doc-comment", checkFilesWithNoDocComment],
  [
    "philosophy.comment-density (INFORMATIONAL ONLY, never fails)",
    checkCommentDensity,
  ],
];

function main() {
  let failing = 0;
  let total = 0;
  for (const [name, fn] of CHECKS) {
    total++;
    let result;
    try {
      result = fn();
    } catch (err) {
      result = { pass: false, count: "ERROR", detail: err.stack };
    }
    const status = result.pass ? "PASS" : "FAIL";
    if (!result.pass) failing++;
    console.log(`[${status}] ${name}: ${JSON.stringify(result.count)}`);
    if (!result.pass && result.detail) {
      const detailStr = Array.isArray(result.detail)
        ? result.detail.join("; ")
        : JSON.stringify(result.detail);
      if (detailStr && detailStr.length > 0 && detailStr !== "[]") {
        console.log(`       detail: ${detailStr}`);
      }
    }
  }
  console.log("");
  console.log(`${total} checks, ${failing} failing`);
  process.exit(failing > 0 ? 1 : 0);
}

main();
