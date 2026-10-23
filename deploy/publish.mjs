// 跨平台发布脚本（Bun）：按 publish.config.json 组装 Windows / macOS 桌面端发布包
// 用法：bun publish.mjs [--force]   （--force 覆盖已存在的输出目录）
// 产物：deploy/out/<rid>/  = 自包含后端 + wwwroot 前端 + data-root（config/vectors/模型/图标目录）+ start 脚本
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

const HERE = import.meta.dirname;
const cfg = JSON.parse(fs.readFileSync(path.join(HERE, "publish.config.json"), "utf8"));
const FORCE = process.argv.includes("--force");
const PIPE = path.resolve(HERE, "..");
const DATA_ROOT = path.resolve(HERE, cfg.dataRoot); // = 新图标（数据根）
const run = (cmd, args, opts = {}) => {
  console.log("  $", cmd, ...args);
  execFileSync(cmd, args, { stdio: opts.quiet ? "ignore" : "inherit", cwd: opts.cwd ?? HERE });
};
const copy = (src, dst) => {
  if (!fs.existsSync(src)) { console.log("  ! 缺少，跳过:", src); return; }
  fs.cpSync(src, dst, { recursive: true, force: true });
};

// 1) 前端构建 → backend/wwwroot（后端静态托管）
console.log("== 前端构建 ==");
const feDir = path.resolve(HERE, "..", "frontend");
run("bun", ["install"], { cwd: feDir, quiet: true });
run(cfg.frontend.buildCmd.split(" ")[0], cfg.frontend.buildCmd.split(" ").slice(1), { cwd: feDir });
const dist = path.resolve(feDir, "dist");
if (!fs.existsSync(dist)) throw new Error("前端构建产物缺失: " + dist);

// 2) dotnet publish（自包含、单 exe 布局）
for (const rid of cfg.runtimes) {
  console.log(`== dotnet publish ${rid} ==`);
  run("dotnet", ["publish", path.resolve(HERE, cfg.backend.project), "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", path.resolve(HERE, "out", rid)], { cwd: PIPE });
}

// 3) 组装每个 rid 的包：wwwroot + data-root + start 脚本
const cfgJson = JSON.parse(fs.readFileSync(path.join(PIPE, "backend/data/config.json"), "utf8"));
for (const rid of cfg.runtimes) {
  console.log(`== 组装 ${rid} ==`);
  const out = path.resolve(HERE, "out", rid);
  // 前端产物
  fs.cpSync(dist, path.join(out, "wwwroot"), { recursive: true, force: true });
  // data-root：backend/data 打平 + 模型 + 图标目录
  const dr = path.join(out, cfg.package.dataRootName);
  fs.mkdirSync(path.join(dr, "icon-picker/backend/data"), { recursive: true });
  for (const rel of cfg.package.includeDataFiles)
    copy(path.join(PIPE, rel), path.join(dr, "icon-picker/backend/data", path.basename(rel)));
  if (cfg.package.includeModels) copy(path.join(PIPE, cfg.backend.models), path.join(dr, "icon-picker/backend/models"));
  // 图标目录：按当前配置真实打包，并把 config.json 中的路径改写为相对 data-root
  const newCfg = { ...cfgJson };
  const mapDir = (absDir) => {
    if (!fs.existsSync(absDir)) return "";
    const name = path.basename(absDir);
    copy(absDir, path.join(dr, name));
    return name; // 相对 data-root
  };
  newCfg.candidateDirs = cfgJson.candidateDirs.map(mapDir);
  newCfg.realDirs = cfgJson.realDirs.map(mapDir);
  fs.writeFileSync(path.join(dr, "icon-picker/backend/data/config.json"), JSON.stringify(newCfg, null, 2));
  // start 脚本：DATA_ROOT 指向包内 data-root
  const exe = rid.startsWith("win") ? "IconPicker.Api.exe" : "IconPicker.Api";
  if (rid.startsWith("win")) {
    fs.writeFileSync(path.join(out, "start.cmd"), `@echo off\r\nset DATA_ROOT=%~dp0${cfg.package.dataRootName}\r\nset ASPNETCORE_URLS=http://localhost:5177\r\n"%~dp0${exe}"\r\n`);
  } else {
    fs.writeFileSync(path.join(out, "start.sh"), `#!/bin/sh\ncd "$(dirname "$0")"\nexport DATA_ROOT="$(pwd)/${cfg.package.dataRootName}"\nexport ASPNETCORE_URLS=http://localhost:5177\nchmod +x "./${exe}"\n"./${exe}"\n`);
    fs.chmodSync(path.join(out, "start.sh"), 0o755);
  }
}
console.log(`完成：deploy/out/{${cfg.runtimes.join(", ")}}`);
