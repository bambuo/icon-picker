// 桌面端发布脚本（Bun）：macOS 产出可双击的 .app 与 .dmg，Windows 产出 zip
// 用法：bun publish.mjs [--rid osx-arm64]   （默认两个 runtime 都打）
// 产物：
//   out/osx-arm64/图标筛选工作台.app
//   out/osx-arm64/图标筛选工作台-macOS-arm64.dmg   （拖入 Applications 即安装）
//   out/win-x64/图标筛选工作台/ + 图标筛选工作台-win-x64.zip
// 包内只有引擎：原生壳 + 自包含单文件后端 + 前端 wwwroot（CLIP 模型由用户在配置里下载）；
// 业务数据（我方模板 / aa123 / OCR / 已采用清单 / 图标目录 / 向量 / 评分结果）一律来自用户选择的工作区。
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

const HERE = import.meta.dirname;
const cfg = JSON.parse(fs.readFileSync(path.join(HERE, "publish.config.json"), "utf8"));
const PIPE = path.resolve(HERE, "..");
const OUT = path.resolve(HERE, cfg.package.outDir);
const DIST = path.resolve(HERE, cfg.frontend.dist);
const onlyIdx = process.argv.indexOf("--rid");
const ONLY = onlyIdx >= 0 ? process.argv[onlyIdx + 1] : null;
const RIDS = ONLY ? cfg.runtimes.filter((r) => r === ONLY) : cfg.runtimes;
if (!RIDS.length) {
  console.error("未知道 runtime:", ONLY);
  process.exit(1);
}

const DOTNET = process.env.DOTNET_ROOT
  ? path.join(process.env.DOTNET_ROOT, "dotnet")
  : fs.existsSync(path.join(os.homedir(), ".dotnet/dotnet"))
    ? path.join(os.homedir(), ".dotnet/dotnet")
    : "dotnet";
const run = (cmd, args, opts = {}) =>
  execFileSync(cmd, args, { stdio: opts.quiet ? "ignore" : "inherit", cwd: opts.cwd ?? HERE });
const copy = (src, dst) => {
  if (!fs.existsSync(src)) {
    console.warn("  ! 缺少，跳过:", src);
    return false;
  }
  fs.cpSync(src, dst, { recursive: true, force: true });
  return true;
};
const rm = (p) => fs.rmSync(p, { recursive: true, force: true });
const exeName = (rid) => (rid.startsWith("win") ? "IconPicker.Api.exe" : "IconPicker.Api");

// 1) 前端构建
console.log("== 前端构建 ==");
const feDir = path.resolve(HERE, "..", "frontend");
run("bun", ["install"], { cwd: feDir, quiet: true });
const [feCmd, ...feArgs] = cfg.frontend.buildCmd.split(" ");
run(feCmd, feArgs, { cwd: feDir });
if (!fs.existsSync(DIST)) throw new Error("前端构建产物缺失: " + DIST);

// 2) 自包含单文件后端
for (const rid of RIDS) {
  console.log(`== dotnet publish ${rid} ==`);
  rm(path.join(OUT, ".publish", rid));
  run(
    DOTNET,
    [
      "publish", path.resolve(HERE, cfg.backend.project),
      "-c", "Release", "-r", rid, "--self-contained", "true",
      "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
      "-o", path.join(OUT, ".publish", rid)
    ],
    { cwd: PIPE }
  );
}

// 后端载荷：单文件 exe + wwwroot + 模型
function buildPayload(dest, rid) {
  rm(dest);
  fs.mkdirSync(dest, { recursive: true });
  fs.cpSync(path.join(OUT, ".publish", rid), dest, { recursive: true, force: true });
  fs.cpSync(DIST, path.join(dest, "wwwroot"), { recursive: true, force: true });
  // 运行期数据在用户目录（DATA_DIR），包内不带；mac 包里的 win 版 onnxruntime DLL 也无用
  rm(path.join(dest, "data"));
  if (!rid.startsWith("win")) {
    rm(path.join(dest, "onnxruntime.dll"));
    rm(path.join(dest, "onnxruntime_providers_shared.dll"));
  }
  // 调试符号/导入库/IIS 配置不进发布包
  for (const f of fs.readdirSync(dest))
    if (/\.(pdb|lib)$/i.test(f) || f === "web.config") rm(path.join(dest, f));
  // OCR 模型改为按需下载：只保留 2KB 字典（识别字符集），ONNX 模型不进包
  const v5 = path.join(dest, "models", "v5");
  if (fs.existsSync(v5)) {
    const dict = path.join(v5, "ppocrv5_latin_dict.txt");
    if (fs.existsSync(dict)) fs.copyFileSync(dict, path.join(dest, "models", "ppocrv5_latin_dict.txt"));
    rm(v5);
  }
  fs.chmodSync(path.join(dest, exeName(rid)), 0o755);
}

// 数据快照不再随包：业务输入由用户在工作区提供（壳首启时选择目录）

function infoPlist() {
  const a = cfg.app;
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${a.name}</string>
  <key>CFBundleDisplayName</key><string>${a.name}</string>
  <key>CFBundleIdentifier</key><string>${a.bundleId}</string>
  <key>CFBundleExecutable</key><string>IconPicker</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${a.version}</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>`;
}

// 3) macOS：.app + .dmg
function buildMac(rid) {
  const name = cfg.app.name;
  rm(path.join(OUT, rid));
  const appDir = path.join(OUT, rid, `${name}.app`);
  const contents = path.join(appDir, "Contents");
  const macos = path.join(contents, "MacOS");
  const resources = path.join(contents, "Resources");
  rm(appDir);
  fs.mkdirSync(macos, { recursive: true });
  fs.mkdirSync(resources, { recursive: true });

  const target = rid === "osx-x64" ? "x86_64-apple-macos12.0" : "arm64-apple-macos12.0";
  console.log(`== 编译原生壳（${target}）==`);
  run("swiftc", ["-swift-version", "5", "-O", path.resolve(HERE, cfg.app.launcher),
    "-o", path.join(macos, "IconPicker"), "-target", target]);

  fs.writeFileSync(path.join(contents, "Info.plist"), infoPlist());
  copy(path.resolve(HERE, cfg.app.icon), path.join(resources, "AppIcon.icns"));
  buildPayload(path.join(resources, "backend"), rid);

  console.log("== ad-hoc 签名 ==");
  run("codesign", ["--force", "--deep", "--sign", "-", appDir]);

  console.log("== 打包 dmg ==");
  const stage = path.join(OUT, rid, ".dmg-stage");
  rm(stage);
  fs.mkdirSync(stage, { recursive: true });
  fs.cpSync(appDir, path.join(stage, `${name}.app`), { recursive: true });
  fs.symlinkSync("/Applications", path.join(stage, "Applications"));
  const dmg = path.join(OUT, rid, `${name}-macOS-${rid === "osx-x64" ? "x64" : "arm64"}.dmg`);
  rm(dmg);
  run("hdiutil", ["create", "-volname", name, "-srcfolder", stage, "-ov", "-format", "UDZO", dmg]);
  rm(stage);
  console.log(`  → ${appDir}\n  → ${dmg}`);
}

// 4) Windows：解压即用的目录 + zip；首次运行弹目录选择框，状态放 %APPDATA%
function buildWin(rid) {
  const name = cfg.app.name;
  rm(path.join(OUT, rid));
  const dir = path.join(OUT, rid, name);
  buildPayload(dir, rid);
  const cmd = String.raw`@echo off
setlocal
cd /d "%~dp0"
set "STATE=%APPDATA%\IconPicker"
if not exist "%STATE%" mkdir "%STATE%"
set "DATA_DIR=%STATE%\data"
set "ASPNETCORE_ENVIRONMENT=Production"
set "ASPNETCORE_URLS=http://127.0.0.1:5177"
start "" powershell -NoProfile -WindowStyle Hidden -Command "for($i=0;$i -lt 120;$i++){try{Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:5177/api/scoring/status' -TimeoutSec 1 | Out-Null; Start-Process 'http://127.0.0.1:5177'; break}catch{Start-Sleep -Milliseconds 500}}"
"IconPicker.Api.exe"
`;
  fs.writeFileSync(path.join(dir, "Start.cmd"), cmd.split("\n").join("\r\n"));
  const zip = path.join(OUT, rid, `${name}-win-x64.zip`);
  rm(zip);
  run("ditto", ["-c", "-k", "--norsrc", "--keepParent", dir, zip]);
  console.log(`  → ${dir}\n  → ${zip}`);
}

for (const rid of RIDS) {
  if (rid.startsWith("osx")) buildMac(rid);
  else if (rid.startsWith("win")) buildWin(rid);
  else throw new Error("不支持的 runtime: " + rid);
}
console.log("完成。");
