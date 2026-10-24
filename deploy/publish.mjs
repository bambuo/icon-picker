// 桌面端发布脚本（Bun）：
//   macOS   → .app + .dmg
//   Windows → 单文件 exe
//   Linux   → AppImage（arm64 / x86_64，Docker 内用 appimagetool 打包）
// 三端都是同一个 Avalonia 进程：壳 + 内置后端 + 前端载荷。
// 用法：bun publish.mjs [--rid osx-arm64] [--skip-linux]
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
const SKIP_LINUX = process.argv.includes("--skip-linux");
let RIDS = ONLY ? cfg.runtimes.filter((r) => r === ONLY) : cfg.runtimes;
if (SKIP_LINUX) RIDS = RIDS.filter((r) => !r.startsWith("linux"));
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
const exeName = (rid) => (rid.startsWith("win") ? "IconPicker.exe" : "IconPicker");
const assets = (f) => path.join(HERE, "assets", f);
const HOST_ARCH = process.arch === "arm64" ? "aarch64" : "x86_64";
const BUILD_PLATFORM = process.arch === "arm64" ? "linux/arm64" : "linux/amd64";

async function download(url, dest) {
  if (fs.existsSync(dest) && fs.statSync(dest).size > 100_000) return;
  console.log("  ↓", url);
  const res = await fetch(url);
  if (!res.ok) throw new Error(`下载失败 ${res.status}: ${url}`);
  const data = Buffer.from(await res.arrayBuffer());
  if (data.length < 100_000) throw new Error("下载内容异常: " + url);
  fs.writeFileSync(dest, data);
}

// 1) 前端构建
console.log("== 前端构建 ==");
const feDir = path.resolve(HERE, "..", "frontend");
run("bun", ["install"], { cwd: feDir, quiet: true });
const [feCmd, ...feArgs] = cfg.frontend.buildCmd.split(" ");
run(feCmd, feArgs, { cwd: feDir });
if (!fs.existsSync(DIST)) throw new Error("前端构建产物缺失: " + DIST);

// 2) 内嵌载荷：前端 + appsettings + OCR 字典
function buildDesktopPayload() {
  console.log("== 组装内嵌载荷 ==");
  const stage = path.join(OUT, ".payload-stage");
  rm(stage);
  fs.mkdirSync(path.join(stage, "models"), { recursive: true });
  fs.cpSync(DIST, path.join(stage, "wwwroot"), { recursive: true, force: true });
  copy(path.join(PIPE, "backend", "appsettings.json"), path.join(stage, "appsettings.json"));
  copy(path.join(PIPE, "backend", "appsettings.Development.json"), path.join(stage, "appsettings.Development.json"));
  copy(assets("ppocrv5_latin_dict.txt"), path.join(stage, "models", "ppocrv5_latin_dict.txt"));
  const zip = path.join(PIPE, "desktop", "desktop-payload.zip");
  rm(zip);
  run("ditto", ["-c", "-k", "--norsrc", stage, zip]);
  rm(stage);
  return zip;
}
buildDesktopPayload();

// 3) 自包含单文件发布（壳 + 后端一体）
for (const rid of RIDS) {
  console.log(`== dotnet publish ${rid} ==`);
  const dest = path.join(OUT, ".publish", rid);
  rm(dest);
  run(DOTNET, [
    "publish", path.resolve(HERE, cfg.desktop.project),
    "-c", "Release", "-r", rid, "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EmbedDesktopPayload=true",
    "-p:DebugType=None", "-p:DebugSymbols=false",
    "-o", dest,
  ], { cwd: PIPE });

  const exe = path.join(dest, exeName(rid));
  if (!fs.existsSync(exe)) throw new Error(`发布产物缺少可执行文件: ${exe}`);
  // 载荷已内嵌，清掉发布目录里重复的配置/静态文件
  for (const f of fs.readdirSync(dest))
    if (/\.(pdb|lib|deps\.json|runtimeconfig\.json)$/i.test(f)
      || f === "web.config"
      || /^appsettings(\..+)?\.json$/i.test(f)) rm(path.join(dest, f));
  for (const d of ["wwwroot", "models", "data"]) rm(path.join(dest, d));
  if (!rid.startsWith("win")) {
    rm(path.join(dest, "onnxruntime.dll"));
    rm(path.join(dest, "onnxruntime_providers_shared.dll"));
  }
  fs.chmodSync(exe, 0o755);
}

function infoPlist() {
  const a = cfg.app;
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${a.name}</string>
  <key>CFBundleDisplayName</key><string>${a.name}</string>
  <key>CFBundleIdentifier</key><string>${a.bundleId}</string>
  <key>CFBundleExecutable</key><string>${a.name}</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${a.version}</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>`;
}

// 4) macOS：.app + .dmg
function buildMac(rid) {
  const name = cfg.app.name;
  rm(path.join(OUT, rid));
  const appDir = path.join(OUT, rid, `${name}.app`);
  const contents = path.join(appDir, "Contents");
  const macos = path.join(contents, "MacOS");
  const resources = path.join(contents, "Resources");
  fs.mkdirSync(macos, { recursive: true });
  fs.mkdirSync(resources, { recursive: true });

  fs.writeFileSync(path.join(contents, "Info.plist"), infoPlist());
  copy(assets("AppIcon.icns"), path.join(resources, "AppIcon.icns"));
  fs.copyFileSync(path.join(OUT, ".publish", rid, "IconPicker"), path.join(macos, name));
  fs.chmodSync(path.join(macos, name), 0o755);

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

// 5) Windows：单个 exe
function buildWin(rid) {
  const name = cfg.app.name;
  rm(path.join(OUT, rid));
  const dir = path.join(OUT, rid);
  fs.mkdirSync(dir, { recursive: true });
  const exe = path.join(dir, `${name}.exe`);
  fs.copyFileSync(path.join(OUT, ".publish", rid, "IconPicker.exe"), exe);
  console.log(`  → ${exe}`);
}

// 6) Linux：AppDir + Docker 内 appimagetool → AppImage
const APP_RUN = `#!/bin/bash
set -e
HERE="$(dirname "$(readlink -f "$0")")"
export GDK_BACKEND="\${GDK_BACKEND:-x11}"
exec "$HERE/usr/lib/icon-picker/IconPicker" "$@"
`;

async function buildLinux(rid) {
  const name = cfg.app.name;
  const arch = rid === "linux-x64" ? "x86_64" : "aarch64";
  rm(path.join(OUT, rid));
  const appDir = path.join(OUT, rid, "AppDir");
  const binDir = path.join(appDir, "usr", "lib", "icon-picker");
  fs.mkdirSync(binDir, { recursive: true });
  fs.copyFileSync(path.join(OUT, ".publish", rid, "IconPicker"), path.join(binDir, "IconPicker"));
  fs.chmodSync(path.join(binDir, "IconPicker"), 0o755);

  fs.writeFileSync(path.join(appDir, "AppRun"), APP_RUN, { mode: 0o755 });
  fs.chmodSync(path.join(appDir, "AppRun"), 0o755);
  fs.writeFileSync(path.join(appDir, "icon-picker.desktop"), `[Desktop Entry]
Type=Application
Name=${name}
Exec=IconPicker
Icon=${name}
Categories=Utility;
Terminal=false
`);
  fs.copyFileSync(assets("AppIcon.png"), path.join(appDir, `${name}.png`));
  fs.copyFileSync(assets("AppIcon.png"), path.join(appDir, ".DirIcon"));

  console.log(`== 准备 AppImage 工具（目标 ${arch}）==`);
  const toolsDir = path.join(OUT, ".appimage");
  fs.mkdirSync(toolsDir, { recursive: true });
  const tool = path.join(toolsDir, "appimagetool");
  const runtime = path.join(toolsDir, `runtime-${arch}`);
  await download(`https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${HOST_ARCH}.AppImage`, tool);
  await download(`https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-${arch}`, runtime);
  fs.chmodSync(tool, 0o755);

  console.log(`== 构建 AppImage（${arch}）==`);
  const dockerfile = path.join(toolsDir, `Dockerfile.${HOST_ARCH}`);
  fs.writeFileSync(dockerfile, `FROM --platform=${BUILD_PLATFORM} debian:bookworm
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends file squashfs-tools && rm -rf /var/lib/apt/lists/*
`);
  const image = `iconpicker-appimage:${HOST_ARCH}`;
  run("docker", ["build", "--platform", BUILD_PLATFORM, "-t", image, "-f", dockerfile, toolsDir], { quiet: true });

  const appimageName = `${name}-linux-${arch}.AppImage`;
  const uid = typeof process.getuid === "function" ? process.getuid() : 0;
  const gid = typeof process.getgid === "function" ? process.getgid() : 0;
  const script = `set -e
chmod +x /out/.appimage/appimagetool
cd "/out/${rid}"
ARCH=${arch} APPIMAGE_EXTRACT_AND_RUN=1 /out/.appimage/appimagetool --runtime-file "/out/.appimage/runtime-${arch}" AppDir "${appimageName}"
`;
  run("docker", [
    "run", "--rm", "--platform", BUILD_PLATFORM, `--user`, `${uid}:${gid}`,
    "-e", "HOME=/tmp", "-v", `${OUT}:/out`, image, "bash", "-lc", script,
  ]);
  const appimage = path.join(OUT, rid, appimageName);
  if (!fs.existsSync(appimage)) throw new Error("AppImage 未生成: " + appimage);
  console.log(`  → ${appimage}`);
}

for (const rid of RIDS) {
  if (rid.startsWith("osx")) buildMac(rid);
  else if (rid.startsWith("win")) buildWin(rid);
  else if (rid.startsWith("linux")) await buildLinux(rid);
  else throw new Error("不支持的 runtime: " + rid);
}
console.log("完成。");
