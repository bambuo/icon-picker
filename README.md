# 图标筛选工作台（icon-picker）

线上「已排序但缺图标」游戏的选图工具：左侧线上现图（static.ppcabt.com 现图），右侧评分 Top5 候选，点选采用并导出 CSV。打分口径见 `../相似度标准.md`。

## 技术栈

- 后端：C# / ASP.NET Core Minimal API（net10.0，无第三方依赖）
- 前端：Vue 3 + TypeScript + Vite + Arco Design Vue，Bun 管理依赖
- 评分引擎：在后端（`ScoringService.cs`，CLIP 余弦 + 后端本地 OCR 文字因子 + SkiaSharp 像素同图，两阶段）；头部「重新分析」触发 `POST /api/scoring/compute`，进度经 `GET /api/scoring/status`，完成后前端自动刷新数据（热更新内存，无需重启）
- 图像向量：由后端 `POST /api/embed/rebuild` 生成（SkiaSharp 预处理 + ONNX Runtime），落 `backend/data/vectors.json`
- 存储：点选记录落 `backend/data/picks.json`；评分留档 `backend/data/visual-results.json`；向量 `backend/data/vectors.json`

## 发布（macOS / Windows 桌面端）

```bash
cd deploy
bun publish.mjs                  # 全量：osx-arm64 + win-x64
bun publish.mjs --rid osx-arm64  # 只打 macOS（迭代时用）
```

包内只有**引擎**：原生壳（WKWebView 窗口 + 启停后端）+ 自包含单文件后端 + 前端 `wwwroot` + OCR 字典（2KB）；不含任何业务数据、不预置任何路径，CLIP 与 OCR 模型都由用户在配置里下载。启动不需要配置：界面立即加载，只是没有数据，路径由用户在工作台「配置」里自行添加。

线上现图/候选图目录、路径规则、CLIP 与 OCR 模型地址都在「配置」里设置。图上文字（OCR）由后端本地完成（PaddleOCR PP-OCRv5 ONNX，Windows/macOS/Linux 通用）：OCR 模型按需下载到运行期数据目录，识别结果缓存为 `ocr-cache.json`，可在「配置 → 图上文字（OCR）」里增量/全量识别。

产物：

- macOS：`out/osx-arm64/图标筛选工作台.app` 与 `图标筛选工作台-macOS-arm64.dmg`。打开 dmg 把 app 拖进 Applications 即安装；双击即进原生窗口，空配置可用；所有路径与模型地址在「配置」抽屉里设置，保存后即时生效（模型/目录变更后按提示补算向量、重新分析）。
- Windows：`out/win-x64/图标筛选工作台/` 与 `图标筛选工作台-win-x64.zip`。解压后双击 `Start.cmd` 立即启动并打开浏览器；所有路径与模型地址在「配置」抽屉里设置。

运行期状态：

- macOS：`~/Library/Application Support/IconPicker/`（`data/` 放 config/picks/vectors/模型/评分结果，`logs/backend.log` 日志，`current-port.txt` 当前端口）。可用 `ICON_PICKER_HOME` 指定状态目录。
- Windows：状态在 `%APPDATA%\IconPicker`，`data-root/` 为空时作为默认数据根。

分发说明：本地构建为 ad-hoc 签名；拷到其他 Mac 首次打开若被 Gatekeeper 拦，执行 `xattr -dr com.apple.quarantine "图标筛选工作台.app"` 或右键打开。监听端口由壳每次自动选空闲端口（开发模式仍是固定 5177）。

macOS 壳源码 `deploy/app/launcher.swift`，应用图标由 `deploy/make-icon.swift` 生成（产物已提交为 `deploy/assets/AppIcon.icns`）。

## 启动

```bash
# 后端（端口 5177）
cd backend
ASPNETCORE_URLS=http://localhost:5177 dotnet run --no-launch-profile

# 前端（端口 5173，/api 与 /img 代理到 5177）
cd frontend
bun install   # 首次
bun dev
```

打开 http://localhost:5173 。开发模式下运行期数据在 `backend/data`，可用 `DATA_DIR` 环境变量覆盖；图片目录与模型地址都在页面「配置」里设置。

## 重新生成打分数据

重算评分：工作台头部「重新分析」按钮（后端两阶段计算，语义排序秒级、像素比对稍慢，进度实时显示）；向量重建：配置抽屉「图像向量」里增量/全量。新增目录/图片后先增量重建向量再重新分析。

注意：`score.mjs` 会同时覆盖 `视觉筛选报告.html` 与 `视觉筛选候选.csv`（与工作台同源同口径）。

## API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/data` | 打分结果全量（199 游戏 × Top5 候选 + 阈值） |
| GET | `/api/picks` | 当前点选 `{game_code: candidate_file}` |
| PUT | `/api/picks/{code}` | body `{"file":"..."}`，候选文件须存在于图标池 |
| DELETE | `/api/picks/{code}` | 取消单个 |
| DELETE | `/api/picks` | 清空 |
| GET | `/api/config` | 读取配置；`livePrefixes` 为 `[{url, name}]`（name 为别名） |
| PUT | `/api/config` | 保存配置；目录地址原样持久化（允许先配置后建目录，使用端跳过不存在的）、前缀去重并兜底命名、默认序号越界重置为 0；`livePathPattern` 为线上现图完整路径规则（默认 `long/{code}.jpg`，必须含 `{code}`），用于按前缀远程取线上现图；`modelUrl` 为 CLIP 模型下载地址、`ocrModelUrl` 为 OCR 模型基础地址、`ocrModelPreset` 为识别模型（latin/ch/en） |
| GET | `/api/embed/status` | 向量重建进度（running/done/total/vectors） |
| POST | `/api/embed/rebuild?force=` | 重建 CLIP 向量（SkiaSharp + ONNX，增量默认，force 全量）；模型未下载时返回 400 |
| POST | `/api/model/download` | 下载 CLIP 模型到运行期数据目录（body 可带 `url`，缺省用配置 `modelUrl`；默认 HuggingFace Xenova/clip-vit-base-patch32） |
| GET | `/api/model/status` | 模型下载进度与是否就绪 |
| POST | `/api/ocr/rebuild?force=` | 跨平台 OCR 识别线上现图/候选图（增量默认，force 全量），结果缓存到 `ocr-cache.json` |
| GET | `/api/ocr/status` | OCR 识别进度、缓存条数与上次更新时间 |
| POST | `/api/ocr/model/download` | 下载 OCR 模型（body 可带 `url` 与 `preset`；preset ∈ latin/ch/en，默认 latin；地址默认 ModelScope PP-OCRv5） |
| GET | `/api/ocr/model/status` | OCR 模型下载进度与是否就绪 |
| GET | `/api/export` | 导出已选 CSV（两列 `game_code,target_file`，其中 target_file = 所选候选图在其候选图目录内的相对地址；UTF-8 BOM + CRLF，附件下载带时间戳文件名；先按当前结果顺序、再补不在本次结果里的点选，不漏记录） |
| GET | `/img/live/{code}.jpg` | 线上现图：按配置目录顺序回退，本地未命中时按前缀（默认优先）远程取图并缓存到第一个目录；远程路径 = 前缀 + `livePathPattern` |

配置改动即时生效（图片响应带 no-store），无需重启；前端「配置」抽屉可视化编辑。 |
| GET | `/img/cand/{file}` | 候选图（`未使用图标/`，按扩展名返回 MIME） |

## 功能

- 分档筛选（同图 / 高相似 / 中相似 / 低相似 / 无线上现图 / 已选）+ 编码/名称/厂商搜索
- 长列表虚拟滚动（只渲染可视区附近卡片，千级数据滚动/浮层不卡顿）
- 点选即持久化（服务端），刷新不丢；卡片头显示「已选 → long/{game_code}.jpg」
- 线上现图 a-image 点击可放大目检；候选区整卡点选
- 亮色 / 暗色主题（Arco `arco-theme`，记忆在浏览器本地）
- 导出 CSV 两列：game_code, target_file（target_file = 所选候选图在其候选图目录内的相对地址）
