# 图标筛选工作台（icon-picker）

按图片编码匹配线上现图与候选图标、生成采用清单的本地工作台。后端 C#（ASP.NET Core Minimal API）、前端 Vue 3 + Arco Design，可打包为 macOS `.app`/`.dmg` 与 Windows 免安装目录。

## 功能

- 每个条目展示线上现图与评分 Top5 候选，候选卡点选采用、再点取消
- 分档筛选（同图 / 高相似 / 中相似 / 低 / 计算中 / 已选）与编码搜索
- 长列表虚拟滚动，千级数据滚动与浮层不卡顿
- 点选服务端持久化；导出 CSV；落盘移动（重命名为 `{game_code}.jpg`、转 JPEG、移动到目标目录）
- CLIP 向量与跨平台 OCR 的模型下载、增量/全量重建与进度展示
- 亮色 / 暗色主题

## 工作原理

- 数据源：线上现图目录（文件名即图片编码）与候选图目录，均在「配置」中指定
- 评分：图像因子 = CLIP 余弦（0.45），文字因子 = 图上 OCR 文本相似度（0.25），缺项时按剩余权重归一；像素三特征（灰度/直方图/dHash）只用于判定「同图」，不参与综合分
- 分档：像素 ≥ 0.78 为同图；综合分 ≥ 0.75 高相似、≥ 0.62 中相似，其余为低
- 「重新分析」两阶段：语义排序（完成即落盘并热更新内存）→ 像素同图比对（后台补充，完成再次热更新）
- 向量与 OCR 结果均缓存在运行期数据目录，评分时直接读取

## 快速开始（开发）

```bash
# 后端（端口 5177）
cd backend
ASPNETCORE_URLS=http://localhost:5177 dotnet run --no-launch-profile

# 前端（端口 5173，/api 与 /img 代理到 5177）
cd frontend
bun install   # 首次
bun dev
```

打开 http://localhost:5173 。开发模式运行期数据在 `backend/data`，可用 `DATA_DIR` 环境变量覆盖。

首次使用顺序：

1. 「配置」里添加线上现图目录、候选图目录（绝对路径），按需配置前缀与路径规则
2. 下载 CLIP 模型 → 增量重建向量
3. （可选）下载 OCR 模型 → 增量/全量识别
4. 点「重新分析」
5. 点选候选 → 导出 CSV 或落盘移动

## 配置项

配置保存在运行期数据的 `config.json`，页面「配置」抽屉可视化编辑，保存即生效（图片响应 `no-store`）。

| 字段 | 说明 |
|---|---|
| `liveDirs` | 线上现图目录，按顺序回退 |
| `candidateDirs` | 候选图目录 |
| `livePrefixes` | 线上现图远程前缀列表 `[{url, name}]`，`defaultPrefixIndex` 指定默认 |
| `livePathPattern` | 线上现图远程路径规则，默认 `long/{code}.jpg`，必须含 `{code}` |
| `modelUrl` | CLIP 模型下载地址（默认 HuggingFace Xenova/clip-vit-base-patch32） |
| `ocrModel` | OCR 识别模型：预设 `latin` / `ch` / `en`，或自定义模型基础地址（http/https，按 latin 识别模型处理） |
| `ocrModelUrl` | OCR 模型默认基础地址（ModelScope PP-OCRv5） |

## 打包发布

```bash
cd deploy
bun publish.mjs                  # 全量：osx-arm64 + win-x64
bun publish.mjs --rid osx-arm64  # 只打 macOS
```

产物：

- macOS：`out/osx-arm64/图标筛选工作台.app` 与 `图标筛选工作台-macOS-arm64.dmg`。拖入 Applications 安装，双击进入原生窗口（WKWebView 壳）
- Windows：`out/win-x64/图标筛选工作台/` 与 `图标筛选工作台-win-x64.zip`。解压双击 `Start.cmd`，就绪后自动打开浏览器

包内只有引擎：原生壳 + 自包含单文件后端 + 前端 `wwwroot` + OCR 字典（2KB）。CLIP 与 OCR 模型不随包，由用户在「配置」里下载。

运行期状态：

- macOS：`~/Library/Application Support/IconPicker/`（`data/` 存 config、picks、vectors、模型、OCR 缓存与评分结果；`runtime/` 为后端工作目录；`logs/backend.log`；`current-port.txt`）
- Windows：`%APPDATA%\IconPicker\`

本地构建为 ad-hoc 签名；拷到其他 Mac 首次打开若被 Gatekeeper 拦截，执行 `xattr -dr com.apple.quarantine "图标筛选工作台.app"` 或右键打开。监听端口由壳自动选择空闲端口（开发模式固定 5177）。

## API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/data` | 评分结果快照与阈值 |
| GET | `/api/config` | 读取配置 |
| PUT | `/api/config` | 保存配置：目录原样持久化、前缀去重、路径规则校验 `{code}`、模型地址校验 http(s) |
| GET | `/api/picks` | 点选集 `{game_code: candidate_file}` |
| PUT | `/api/picks/{code}` | body `{"file":"..."}`，候选文件须存在于候选目录 |
| DELETE | `/api/picks/{code}` | 取消单个点选 |
| DELETE | `/api/picks` | 清空点选 |
| POST | `/api/scoring/compute` | 重新分析（先增量补算向量再评分） |
| GET | `/api/scoring/status` | 评分阶段与进度 |
| GET | `/api/embed/status` | 向量进度、向量数、上次重建时间 |
| POST | `/api/embed/rebuild?force=` | 重建 CLIP 向量（增量默认，force 全量） |
| POST | `/api/model/download` | 下载 CLIP 模型，body 可带 `url` |
| GET | `/api/model/status` | CLIP 模型下载进度与是否就绪 |
| POST | `/api/ocr/rebuild?force=` | OCR 识别线上现图/候选图（增量默认） |
| GET | `/api/ocr/status` | OCR 进度、缓存条数、上次识别时间 |
| POST | `/api/ocr/model/download` | 下载 OCR 模型，body 可带 `model`（预设或地址） |
| GET | `/api/ocr/model/status` | OCR 模型下载进度与是否就绪 |
| GET | `/api/export` | 导出 CSV（`game_code,target_file`，target_file 为候选图在候选目录内的相对地址；UTF-8 BOM + CRLF） |
| POST | `/api/adopt/move` | 落盘移动，body 可带 `targetDir`，默认 `~/Desktop/{yyyyMMddHHmmss}` |
| GET | `/api/fs/dirs?path=&all=` | 目录浏览（前端选路径），`all=true` 显示隐藏目录 |
| GET | `/img/live/{code}.jpg` | 线上现图：本地目录回退，未命中按前缀远程取图并缓存到第一个目录 |
| GET | `/img/cand/{file}` | 候选图 |

## 目录结构

```
backend/            ASP.NET Core 后端（Program.cs 路由、ScoringService 评分、ClipEmbedding 向量、OcrService 识别）
frontend/           Vue 3 + Vite + Arco 工作台界面
deploy/             publish.mjs 打包脚本、launcher.swift 原生壳、图标与发布配置
icon-picker.slnx    .NET 解决方案文件
```

## 已知限制

- CLIP 与 OCR 模型需联网下载（地址可改）；OCR 内置 PP-OCRv5 的 Latin / 中文 / 英文识别模型
- OCR 对高度风格化的艺术字识别有限，文字因子缺失时评分自动只用图像分
- 权重与阈值是经验值，集中在 `ScoringService.cs` 常量中
