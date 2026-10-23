import fs from "node:fs";
import path from "node:path";
import { execFile } from "node:child_process";
import os from "node:os";

const SRC_LIST = "/Users/johana/Desktop/新图标/t_game_template_202610191458.json";
const AA_DIR = "/Users/johana/Desktop/aa123";
const OUT_DIR = "/Users/johana/Desktop/新图标";
const APPLY = process.argv.includes("--apply");

const HALL_GROUP = {
  FC: "FC", PGS: "PG", HPG: "PG", TD: "TaDa", WG: "WG", PP: "PP", CQ9: "CQ9",
  EVOPLAY: "Evoplay", G759: "Slot", JDB: "JDB", POPOK: "POPOK", HSD: "HackSaw", GPKBG: "BGaming",
};

const nameOf = (e) => {
  try { return JSON.parse(e.game_name).pt || ""; } catch { return e.game_name || ""; }
};
const key = (s) => String(s).toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "")
  .replace(/[“”'’]/g, "").replace(/[^a-z0-9]+/g, "");
const stripRtp = (s) => String(s).replace(/\s*(?:[-–—_]\s*)?(?:8[5-9]|9[0-9]|100)\s*$/, "");

const ours = JSON.parse(fs.readFileSync(SRC_LIST, "utf8")).t_game_template;
const aaRaw = JSON.parse(fs.readFileSync(path.join(AA_DIR, "games.json"), "utf8"));
const games = Array.isArray(aaRaw) ? aaRaw : aaRaw.games;

const idx = {};
const globalIdx = {};
for (const g of games) {
  const byGroup = (idx[g.group_name] = idx[g.group_name] || { exact: {}, rtp: {} });
  (byGroup.exact[key(g.game_name)] ||= []).push(g);
  const rk = key(stripRtp(g.game_name));
  if (rk) (byGroup.rtp[rk] ||= []).push(g);
  (globalIdx[key(g.game_name)] ||= []).push(g);
}

const otherGroupHits = (raw, groupName) => globalIdx[key(raw)]?.filter((g) => g.group_name !== groupName) || [];

const matched = [], unmatched = [];
for (const e of ours) {
  const groupName = HALL_GROUP[e.hall_code];
  const raw = nameOf(e);
  const target = path.basename(e.default_icon || `${e.game_code}.jpg`);
  const candidates = otherGroupHits(raw, groupName);
  if (!groupName) { unmatched.push({ e, raw, target, candidates, reason: "hall 无对应分组" }); continue; }
  const byGroup = idx[groupName] || { exact: {}, rtp: {} };
  const hits = byGroup.exact[key(raw)] || byGroup.rtp[key(stripRtp(raw))] || [];
  if (!hits.length) { unmatched.push({ e, raw, target, candidates, reason: "同厂商分组内无同名游戏" }); continue; }
  const viaRtp = !byGroup.exact[key(raw)];
  matched.push({ e, raw, target, group: groupName, aa: hits[0], viaRtp });
}

const byTarget = {};
for (const m of matched) (byTarget[m.target] ||= []).push(m);
const collisions = Object.entries(byTarget).filter(([, v]) => new Set(v.map((m) => m.aa.game_key)).size > 1);

console.log(`我方 ${ours.length} 行 | 命中 ${matched.length} | 未命中 ${unmatched.length}`);
console.log(`  其中 RTP 变体回落命中 ${matched.filter((m) => m.viaRtp).length}`);
console.log(`  唯一源图标 ${new Set(matched.map((m) => m.aa.icon_file)).size} 个，目标文件 ${new Set(matched.map((m) => m.target)).size} 个`);
if (collisions.length) console.log("  冲突(同一目标名对应多个源图标):", collisions.map(([t, v]) => `${t}<-[${v.map((m) => m.aa.game_key).join(",")}]`).join(" "));

const count = (arr, f) => arr.reduce((m, x) => ((m[f(x)] = (m[f(x)] || 0) + 1), m), {});
console.log("\n按 hall:");
const mt = count(matched, (m) => m.e.hall_code), um = count(unmatched, (u) => u.e.hall_code);
for (const h of Object.keys(HALL_GROUP)) console.log(`  ${h.padEnd(9)} 命中 ${String(mt[h] || 0).padStart(4)} / 未命中 ${String(um[h] || 0).padStart(4)}  → ${HALL_GROUP[h]}`);
const noMap = new Set(unmatched.filter((u) => !HALL_GROUP[u.e.hall_code]).map((u) => u.e.hall_code));
if (noMap.size) console.log("  无分组映射的 hall:", [...noMap].join(", "));

const sample = (arr, n) => arr.slice(0, n).map((m) => `     ${m.e.hall_code.padEnd(8)} ${m.raw.padEnd(38)} ~ ${m.aa.game_name.padEnd(38)} ${m.aa.game_key}${m.viaRtp ? " (RTP回落)" : ""}`).join("\n");
console.log("\nRTP 回落样例:\n" + sample(matched.filter((m) => m.viaRtp), 25));
console.log("\n未命中样例(有映射的 hall):\n" + sample(unmatched.filter((u) => HALL_GROUP[u.e.hall_code]).slice(0, 25).map((u) => ({ e: u.e, raw: u.raw, aa: { game_name: "-", game_key: "-" }, viaRtp: false })), 25));

const csv = (rows) => "\ufeff" + rows.map((r) => r.map((c) => `"${String(c ?? "").replace(/"/g, '""')}"`).join(",")).join("\r\n");
if (!APPLY) {
  console.log("\n[dry-run] 只读预演：不落地图标、不写报告。加 --apply 执行转码复制（仅复制，aa123 原件不动）。");
  process.exit(0);
}
fs.writeFileSync(path.join(OUT_DIR, "匹配结果.csv"), csv([
  ["hall_code", "game_code", "我方名称", "目标文件", "aa123分组", "aa123游戏名", "aa123_game_key", "匹配方式", "源图标"],
  ...matched.map((m) => [m.e.hall_code, m.e.game_code, m.raw, `long/${m.target}`, m.group, m.aa.game_name, m.aa.game_key, m.viaRtp ? "RTP变体回落" : "同名", m.aa.icon_file]),
]));
fs.writeFileSync(path.join(OUT_DIR, "未匹配清单.csv"), csv([
  ["hall_code", "game_code", "我方名称", "预期目标文件", "原因", "跨厂商同名候选(未落地)"],
  ...unmatched.map((u) => [u.e.hall_code, u.e.game_code, u.raw, `long/${u.target}`, u.reason,
    u.candidates.map((c) => `${c.game_key} ${c.game_name}`).join("; ")]),
]));
console.log(`\n已写 匹配结果.csv (${matched.length} 行) 与 未匹配清单.csv (${unmatched.length} 行)`);
const withCand = unmatched.filter((u) => u.candidates.length).length;
console.log(`  未匹配中有跨厂商同名候选的: ${withCand} 行（仅登记，未取图）`);

if (!APPLY) { console.log("\n[dry-run] 未落地图标，加 --apply 执行转码复制。"); process.exit(0); }

const cache = path.join(os.tmpdir(), "aa123-jpeg-cache");
fs.mkdirSync(cache, { recursive: true });
fs.mkdirSync(path.join(OUT_DIR, "long"), { recursive: true });

const convert = (src) => new Promise((resolve, reject) => {
  const out = path.join(cache, key(path.basename(src)) + ".jpg");
  if (fs.existsSync(out)) return resolve(out);
  execFile("sips", ["-s", "format", "jpeg", "-s", "formatOptions", "90", src, "--out", out], (err) => err ? reject(new Error(`${src}: ${err.message}`)) : resolve(out));
});

const uniq = [...new Set(matched.map((m) => path.join(AA_DIR, m.aa.icon_file)))];
let done = 0, files = 0;
for (let i = 0; i < uniq.length; i += 8) {
  const batch = uniq.slice(i, i + 8);
  const outs = await Promise.all(batch.map(convert));
  for (let j = 0; j < outs.length; j++) {
    const t = matched.filter((m) => path.join(AA_DIR, m.aa.icon_file) === batch[j]).map((m) => m.target);
    for (const target of t) { await fs.promises.copyFile(outs[j], path.join(OUT_DIR, "long", target)); files++; }
    if (++done % 400 === 0) console.log(`  进度 ${done}/${uniq.length} 源图标, ${files} 目标文件`);
  }
}
console.log(`完成：${done} 个源图标转码，${files} 个目标文件写入 ${OUT_DIR}/long/`);
