# 图标筛选工作台（icon-picker）

线上「已排序但缺图标」游戏的选图工具：左侧真实图（static.ppcabt.com 现图），右侧三因子打分 Top5 候选，点选采用并导出 CSV。打分口径见 `../相似度标准.md`。

## 技术栈

- 后端：C# / ASP.NET Core Minimal API（net10.0，无第三方依赖）
- 前端：Vue 3 + TypeScript + Vite + Arco Design Vue，Bun 管理依赖
- 评分引擎：在后端（`ScoringService.cs`，CLIP 余弦 + 名字/OCR 文字因子 + SkiaSharp 像素同图，两阶段）；头部「重新分析」触发 `POST /api/scoring/compute`，进度经 `GET /api/scoring/status`，完成后前端自动刷新数据（热更新内存，无需重启）
- 打分管线遗留：`pipeline/`（build-icons.mjs 厂商同名匹配 / ocr.swift Vision OCR 语料 / fetch-real-icons.go 并发下载；OCR 语料在 `pipeline/data/ocr/`）
- 图像向量：由后端 `POST /api/embed/rebuild` 生成（SkiaSharp 预处理 + ONNX Runtime），落 `backend/data/vectors.json`
- 存储：点选记录落 `backend/data/picks.json`；评分留档 `backend/data/visual-results.json`；向量 `backend/data/vectors.json`

## 发布（Windows / macOS 桌面端）

```bash
cd deploy
bun publish.mjs            # 按 publish.config.json 组装 out/{osx-arm64, win-x64}
```

产物布局（每 runtime 一目录）：自包含后端二进制 + `wwwroot/`（前端静态，后端托管 + SPA 回退）+ `data-root/`（config/picks/vectors/visual-results + models + 图标目录，config 内路径已改写为相对）+ `start.sh`/`start.cmd`（内置 DATA_ROOT 与端口 5177）。发布含数据；工作台「配置」可随时改目录。启动自检：向量缺失自动增量重建 + 自动重算评分（appsettings `AutoInit` 可关）。

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

打开 http://localhost:5173 。数据根目录默认 `/Users/johana/Desktop/新图标`，可用 `DataRoot` 配置项覆盖。

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
| GET | `/api/config` | 读取配置；`realPrefixes` 为 `[{url, name}]`（name 为别名），旧版纯 URL 数组读取时自动迁移 |
| PUT | `/api/config` | 保存配置；目录地址原样持久化（允许先配置后建目录，使用端跳过不存在的）、前缀去重并兜底命名、默认序号越界重置为 0 |
| GET | `/api/config` | 见上；`realPrefixes` 支持 `[{url,name}]` 别名 |
| GET | `/api/embed/status` | 向量重建进度（running/done/total/vectors） |
| POST | `/api/embed/rebuild?force=` | 重建 CLIP 向量（SkiaSharp + ONNX，增量默认，force 全量） |
| GET | `/api/export` | 导出选择 CSV（UTF-8 BOM，含 `long/{game_code}.jpg` 目标名） |
| GET | `/img/real/{code}.jpg` | 真实图：按配置目录顺序回退，本地未命中时按前缀（默认优先）远程取图并缓存到第一个目录；icon 路径按模板 default_icon 解析 |

配置改动即时生效（图片响应带 no-store），无需重启；前端「配置」抽屉可视化编辑。 |
| GET | `/img/cand/{file}` | 候选图（`未使用图标/`，按扩展名返回 MIME） |

## 功能

- 分档筛选（同图 / 可直接采用 / 建议目检 / 低 / 无真实图 / 已选）+ 编码/名称/厂商搜索
- 点选即持久化（服务端），刷新不丢；卡片头显示「已选 → long/{game_code}.jpg」
- 真实图 a-image 点击可放大目检；候选区整卡点选
- 亮色 / 暗色主题（Arco `arco-theme`，记忆在浏览器本地）
- 导出 CSV 列：game_code、hall、name、candidate_file、aa123_group、aa123_name、total、target_file
