using System.Text;
using System.Text.Json;
using IconPicker.Api;
using SkiaSharp;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient("img", c => c.Timeout = TimeSpan.FromSeconds(8));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/img")
        && ctx.Request.Method == "GET" && !Path.HasExtension(ctx.Request.Path.Value ?? ""))
    {
        var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
        if (File.Exists(index)) { ctx.Response.ContentType = "text/html; charset=utf-8"; await ctx.Response.SendFileAsync(index); return; }
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/img"))
        ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(app.Environment.ContentRootPath, "data");
var vecPath = Path.Combine(dataDir, "vectors.json");
var picksPath = Path.Combine(dataDir, "picks.json");
var configPath = Path.Combine(dataDir, "config.json");
var resultsPath = Path.Combine(dataDir, "visual-results.json");
OcrService.Configure(dataDir);

const string DefaultModelUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model.onnx";
const string DefaultOcrModelUrl = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5";

// ---------- config ----------
// 不做任何预置：目录/前缀都由用户在工作台里配置；无配置也能启动（界面空数据）
var config = new Config();
if (File.Exists(configPath))
{
    var node = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject();
    // 旧字段迁移：realXxx → liveXxx（线上现图改名）
    var migrated = false;
    foreach (var (oldKey, newKey) in new[] { ("realDirs", "liveDirs"), ("realPrefixes", "livePrefixes"), ("realPathPattern", "livePathPattern") })
        if (node[oldKey] is { } v && node[newKey] is null) { node.Remove(oldKey); node[newKey] = v.DeepClone(); migrated = true; }
    config = node.Deserialize<Config>(JsonOpt()) ?? config;
    if (migrated) SaveConfig();
}
else
    SaveConfig();
if (string.IsNullOrWhiteSpace(config.ModelUrl)) config.ModelUrl = DefaultModelUrl;
if (string.IsNullOrWhiteSpace(config.OcrModelUrl)) config.OcrModelUrl = DefaultOcrModelUrl;
if (string.IsNullOrWhiteSpace(config.OcrModel)) config.OcrModel = "latin";
// 发布包内的 config 用相对 DATA_ROOT 的目录名，启动时统一解析为绝对路径
config.CandidateDirs = config.CandidateDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(NormDir).Distinct().ToList();
config.LiveDirs = config.LiveDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(NormDir).Distinct().ToList();
void SaveConfig() { Directory.CreateDirectory(dataDir); File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOpt(true))); }
JsonSerializerOptions JsonOpt(bool indented = false) => new()
{
    WriteIndented = indented,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// ---------- 线上现图完整路径规则：{code} 替换为游戏编码；用于按前缀远程取图 ----------
string LivePathOf(string code)
{
    var pattern = string.IsNullOrWhiteSpace(config.LivePathPattern) ? "long/{code}.jpg" : config.LivePathPattern;
    return pattern.Replace("{code}", Path.GetFileName(code), StringComparison.OrdinalIgnoreCase);
}

// ---------- 数据与点选 ----------
// 发布包不带评分结果：首次启动先给空结果，自检算完再热更新
const string EmptyResults = """{"high":0.75,"mid":0.62,"sameImg":0.78,"poolCount":0,"results":[]}""";
var resultsRaw = File.Exists(resultsPath) ? File.ReadAllText(resultsPath) : EmptyResults;
// 旧评分快照字段迁移：hasReal → hasLive
if (resultsRaw.Contains("\"hasReal\""))
{
    resultsRaw = resultsRaw.Replace("\"hasReal\"", "\"hasLive\"");
    try { File.WriteAllText(resultsPath, resultsRaw); } catch { /* 快照只读时忽略 */ }
}
JsonElement data = JsonDocument.Parse(resultsRaw).RootElement.Clone();
JsonElement games = data.GetProperty("results");
var gameByCode = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
foreach (var g in games.EnumerateArray())
    gameByCode[g.GetProperty("code").GetString()!] = g;
void LoadScoring(string json)
{
    data = JsonDocument.Parse(json).RootElement.Clone();
    games = data.GetProperty("results");
    gameByCode.Clear();
    foreach (var g in games.EnumerateArray())
        gameByCode[g.GetProperty("code").GetString()!] = g;
}

Dictionary<string, string> LoadPicks() =>
    File.Exists(picksPath)
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(picksPath)) ?? new()
        : new();
void SavePicks(Dictionary<string, string> picks)
{
    Directory.CreateDirectory(dataDir);
    File.WriteAllText(picksPath, JsonSerializer.Serialize(picks, JsonOpt(true)));
}

IResult J(object o, int status = 200)
{
    var body = JsonSerializer.Serialize(o, JsonOpt());
    return Results.Content(body, "application/json; charset=utf-8", statusCode: status);
}

app.MapGet("/api/data", () => J(data));
app.MapGet("/api/config", () => J(config));
app.MapPut("/api/config", async (HttpRequest req) =>
{
    var incoming = await JsonSerializer.DeserializeAsync<Config>(req.Body, JsonOpt());
    if (incoming is null) return J(new { error = "bad config" }, 400);
    // 允许空配置：目录与前缀都由用户按需添加
    incoming.LivePrefixes = incoming.LivePrefixes
        .Where(p => !string.IsNullOrWhiteSpace(p.Url))
        .Select(p => new PrefixItem(p.Url.Trim().TrimEnd('/'), string.IsNullOrWhiteSpace(p.Name) ? "未命名" : p.Name.Trim()))
        .DistinctBy(p => p.Url)
        .ToList();
    if (incoming.DefaultPrefixIndex < 0 || incoming.DefaultPrefixIndex >= incoming.LivePrefixes.Count)
        incoming.DefaultPrefixIndex = 0;
    incoming.LivePathPattern = string.IsNullOrWhiteSpace(incoming.LivePathPattern)
        ? "long/{code}.jpg" : incoming.LivePathPattern.Trim();
    if (!incoming.LivePathPattern.Contains("{code}", StringComparison.OrdinalIgnoreCase))
        return J(new { error = "线上现图路径规则需包含 {code} 占位符" }, 400);
    if (string.IsNullOrWhiteSpace(incoming.ModelUrl)) incoming.ModelUrl = DefaultModelUrl;
    incoming.ModelUrl = incoming.ModelUrl.Trim();
    if (!incoming.ModelUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return J(new { error = "模型下载地址需以 http(s):// 开头" }, 400);
    if (string.IsNullOrWhiteSpace(incoming.OcrModelUrl)) incoming.OcrModelUrl = DefaultOcrModelUrl;
    incoming.OcrModelUrl = incoming.OcrModelUrl.Trim();
    if (!incoming.OcrModelUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return J(new { error = "OCR 模型地址需以 http(s):// 开头" }, 400);
    incoming.OcrModelPreset = OcrService.PresetKeys.Contains(incoming.OcrModelPreset ?? "", StringComparer.OrdinalIgnoreCase)
        ? incoming.OcrModelPreset!.ToLowerInvariant() : "latin";
    incoming.OcrModel = (incoming.OcrModel ?? "").Trim();
    if (incoming.OcrModel.Length == 0) incoming.OcrModel = "latin";
    if (!incoming.OcrModel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && !OcrService.PresetKeys.Contains(incoming.OcrModel, StringComparer.OrdinalIgnoreCase))
        incoming.OcrModel = "latin";
    // 目录地址原样持久化（允许先配置后建目录）；使用端按需跳过不存在的目录
    incoming.CandidateDirs = incoming.CandidateDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(NormDir).Distinct().ToList();
    incoming.LiveDirs = incoming.LiveDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(NormDir).Distinct().ToList();
    config = incoming;
    SaveConfig();
    return J(config);
});
string NormDir(string p) => Path.TrimEndingDirectorySeparator(Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(dataDir, p)));

app.MapGet("/api/picks", () => J(LoadPicks()));
app.MapPut("/api/picks/{code}", async (string code, HttpRequest req) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    if (!doc.RootElement.TryGetProperty("file", out var fileEl)) return J(new { error = "missing file" }, 400);
    var file = fileEl.GetString() ?? "";
    var safe = Path.GetFileName(file);
    if (!config.CandidateDirs.Any(d => File.Exists(Path.Combine(d, safe))))
        return J(new { error = "candidate not found" }, 400);
    var picks = LoadPicks();
    picks[code] = safe;
    SavePicks(picks);
    return J(picks);
});
app.MapDelete("/api/picks/{code}", (string code) =>
{
    var picks = LoadPicks();
    picks.Remove(code);
    SavePicks(picks);
    return J(picks);
});
app.MapDelete("/api/picks", () => { SavePicks(new()); return J(new Dictionary<string, string>()); });

// ---------- CLIP 向量 ----------
static int VectorCount(string path)
{
    try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))?.Count ?? 0; }
    catch { return 0; }
}
// 模型放运行期数据目录（发布包不再携带）；开发机旧位置只读兜底
var modelPath = Path.Combine(dataDir, "models", "vision_model.onnx");
var bundledModelPath = Path.Combine(app.Environment.ContentRootPath, "models", "vision_model.onnx");
if (!File.Exists(modelPath) && File.Exists(bundledModelPath)) modelPath = bundledModelPath;
var modelDl = new ModelDownloadState();

app.MapGet("/api/embed/status", () => J(new
{
    ClipEmbedding.Running,
    ClipEmbedding.Done,
    ClipEmbedding.Total,
    ClipEmbedding.Error,
    ClipEmbedding.StartedAt,
    ClipEmbedding.FinishedAt,
    modelExists = File.Exists(modelPath),
    vectors = File.Exists(vecPath) ? VectorCount(vecPath) : 0,
    updatedAt = File.Exists(vecPath) ? File.GetLastWriteTime(vecPath) : (DateTime?)null,
}));

app.MapPost("/api/embed/rebuild", (HttpRequest req) =>
{
    if (!File.Exists(modelPath)) return J(new { error = "模型缺失：请先在「配置 → 图像向量」下载模型" }, 400);
    if (ClipEmbedding.Running) return J(new { error = "重建已在进行中" }, 409);
    var force = bool.TryParse(req.Query["force"].ToString(), out var f) && f;
    Task.Run(() => ClipEmbedding.Rebuild(dataDir, modelPath, force));
    return J(new { started = true, force });
});

app.MapPost("/api/model/download", async (HttpRequest req) =>
{
    if (modelDl.Running) return J(new { error = "模型下载已在进行中" }, 409);
    string? url = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("url", out var u))
            url = u.GetString();
    }
    catch { /* 无 body 时用配置值 */ }
    if (!string.IsNullOrWhiteSpace(url)) config.ModelUrl = url.Trim();
    if (string.IsNullOrWhiteSpace(config.ModelUrl)) config.ModelUrl = DefaultModelUrl;
    if (!config.ModelUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return J(new { error = "模型下载地址需以 http(s):// 开头" }, 400);

    var theUrl = config.ModelUrl;
    Task.Run(async () =>
    {
        modelDl.Running = true; modelDl.Done = 0; modelDl.Total = 0; modelDl.Error = null;
        modelDl.StartedAt = DateTime.Now; modelDl.FinishedAt = null;
        var dest = Path.Combine(dataDir, "models", "vision_model.onnx");
        var tmp = dest + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = await http.GetAsync(theUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            modelDl.Total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var fs = File.Create(tmp))
            {
                var buf = new byte[128 * 1024];
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n));
                    modelDl.Done += n;
                }
            }
            File.Move(tmp, dest, true);
            modelPath = dest;
            modelDl.FinishedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            modelDl.Error = ex.Message;
            try { File.Delete(tmp); } catch { }
        }
        finally { modelDl.Running = false; }
    });
    return J(new { started = true, url = theUrl });
});

app.MapGet("/api/model/status", () => J(new
{
    modelDl.Running, modelDl.Done, modelDl.Total, modelDl.Error, modelDl.StartedAt, modelDl.FinishedAt,
    modelExists = File.Exists(modelPath), modelPath, url = config.ModelUrl,
}));

app.MapPost("/api/ocr/rebuild", (HttpRequest req) =>
{
    if (OcrService.Running) return J(new { error = "OCR 已在进行中" }, 409);
    var force = bool.TryParse(req.Query["force"].ToString(), out var f) && f;
    Task.Run(() => OcrService.Rebuild(
        [.. config.LiveDirs, .. config.CandidateDirs], dataDir, force));
    return J(new { started = true, force });
});

app.MapGet("/api/ocr/status", () =>
{
    var cache = OcrService.CachePath(dataDir);
    return J(new
    {
        OcrService.Running, OcrService.Done, OcrService.Total, OcrService.Error,
        OcrService.StartedAt, OcrService.FinishedAt,
        count = OcrService.LoadCache(dataDir).Count,
        updatedAt = File.Exists(cache) ? File.GetLastWriteTime(cache) : (DateTime?)null,
    });
});

app.MapPost("/api/ocr/model/download", async (HttpRequest req) =>
{
    if (OcrService.Downloading) return J(new { error = "OCR 模型下载已在进行中" }, 409);
    string? url = null; string? preset = null; string? model = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
            if (doc.RootElement.TryGetProperty("preset", out var p)) preset = p.GetString();
            if (doc.RootElement.TryGetProperty("model", out var m)) model = m.GetString();
        }
    }
    catch { /* 无 body 时用配置值 */ }
    if (!string.IsNullOrWhiteSpace(url)) config.OcrModelUrl = url.Trim();
    if (!string.IsNullOrWhiteSpace(preset)) config.OcrModelPreset = preset.Trim();
    if (!string.IsNullOrWhiteSpace(model)) config.OcrModel = model.Trim();
    if (string.IsNullOrWhiteSpace(config.OcrModel)) config.OcrModel = config.OcrModelPreset;
    if (string.IsNullOrWhiteSpace(config.OcrModelUrl)) config.OcrModelUrl = DefaultOcrModelUrl;

    var isCustomUrl = config.OcrModel.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    if (isCustomUrl)
    {
        if (!Uri.TryCreate(config.OcrModel, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            return J(new { error = "OCR 模型地址需以 http(s):// 开头" }, 400);
    }

    var theUrl = isCustomUrl ? config.OcrModel : config.OcrModelUrl;
    var thePreset = isCustomUrl ? "latin"
        : (OcrService.PresetKeys.Contains(config.OcrModel, StringComparer.OrdinalIgnoreCase) ? config.OcrModel.ToLowerInvariant() : "latin");
    Task.Run(() => OcrService.DownloadModels(theUrl, thePreset, dataDir));
    return J(new { started = true, url = theUrl, preset = thePreset, model = config.OcrModel });
});

app.MapGet("/api/ocr/model/status", () => J(new
{
    OcrService.Downloading, OcrService.DlDone, OcrService.DlTotal, OcrService.DlError,
    OcrService.DlStartedAt, OcrService.DlFinishedAt,
    modelReady = OcrService.ModelsReady(dataDir),
    dir = OcrService.ActiveModelDir(dataDir),
    url = config.OcrModelUrl,
    preset = config.OcrModelPreset,
    model = config.OcrModel,
}));

// ---------- 评分模块数据源（评分在前端浏览器内执行） ----------
// ---------- 评分（后端计算，两阶段） ----------
app.MapPost("/api/scoring/compute", (HttpRequest req) =>
{
    if (ScoringService.Running) return J(new { error = "评分已在进行中" }, 409);
    if (ClipEmbedding.Running) return J(new { error = "向量补算进行中" }, 409);
    var force = bool.TryParse(req.Query["force"].ToString(), out var f) && f;
    Task.Run(() =>
    {
        // 工作区随时可能新增图片：先增量补算缺失向量，再评分
        ClipEmbedding.Rebuild(dataDir, modelPath, false);
        ScoringService.Compute(
            config.LiveDirs.ToList().AsReadOnly(), config.CandidateDirs.ToList().AsReadOnly(),
            OcrService.LoadTexts(dataDir),
            vecPath, resultsPath, LoadScoring);
    });
    return J(new { started = true, force });
});

app.MapGet("/api/scoring/status", () => J(new
{
    ScoringService.Running,
    Phase = ScoringService.Phase,
    Done = ScoringService.Done,
    Total = ScoringService.Total,
    ScoringService.Error,
    ScoringService.StartedAt,
    ScoringService.FinishedAt,
}));

// ---------- 文件系统目录浏览（前端选路径用） ----------
app.MapGet("/api/fs/dirs", (string? path, bool? all) =>
{
    var dir = string.IsNullOrWhiteSpace(path)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : Path.GetFullPath(path);
    if (!Directory.Exists(dir)) return J(new { error = "目录不存在" }, 400);
    var showAll = all == true;
    var dirs = Directory.EnumerateDirectories(dir)
        .Where(d => showAll || !Path.GetFileName(d).StartsWith("."))
        .Select(Path.GetFileName)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var parent = Directory.GetParent(dir)?.FullName;
    return J(new { path = dir, parent, dirs });
});

app.MapGet("/img/live/{*path}", async (string path) =>
{
    var name = Path.GetFileName(path);
    if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
    var safe = name + ".jpg";

    foreach (var dir in config.LiveDirs)
    {
        var p = Path.Combine(dir, safe);
        if (File.Exists(p)) return Results.File(p, "image/jpeg");
    }

    // 本地未命中 → 依次尝试远程前缀（默认优先），成功后缓存到第一个线上现图目录
    var prefixes = config.LivePrefixes.ToList();
    if (config.DefaultPrefixIndex > 0 && config.DefaultPrefixIndex < prefixes.Count)
        prefixes = [prefixes[config.DefaultPrefixIndex], .. prefixes.Where((_, i) => i != config.DefaultPrefixIndex)];
    var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("img");
    var iconPath = LivePathOf(name);
    foreach (var prefix in prefixes)
    {
        try
        {
            var url = prefix.Url.TrimEnd('/') + "/" + iconPath.TrimStart('/');
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) continue;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (config.LiveDirs.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(config.LiveDirs[0]);
                    await File.WriteAllBytesAsync(Path.Combine(config.LiveDirs[0], safe), bytes);
                }
                catch { /* 缓存失败不影响返回 */ }
            }
            return Results.File(bytes, "image/jpeg");
        }
        catch { /* 下一个前缀 */ }
    }
    return Results.NotFound();
});

app.MapGet("/img/cand/{file}", (string file) =>
{
    foreach (var dir in config.CandidateDirs)
    {
        var fp = Path.Combine(dir, Path.GetFileName(file));
        if (!File.Exists(fp)) continue;
        var type = fp.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                 : fp.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";
        return Results.File(fp, type);
    }
    return Results.NotFound();
});

// ---------- 启动自检：向量增量补算 + 评分重算（发布口径，appsettings AutoInit 可关） ----------
// 未配置任何目录时跳过，保证空配置也能秒开
if (app.Configuration.GetValue("AutoInit", true)
    && (config.LiveDirs.Count > 0 || config.CandidateDirs.Count > 0))
{
    Task.Run(() =>
    {
        try
        {
            if (!File.Exists(modelPath)) return;
            ClipEmbedding.Rebuild(dataDir, modelPath, false); // 增量：已有键跳过
            ScoringService.Compute(
                config.LiveDirs.ToList().AsReadOnly(), config.CandidateDirs.ToList().AsReadOnly(),
                OcrService.LoadTexts(dataDir),
                vecPath, resultsPath, LoadScoring);
        }
        catch (Exception ex) { Console.Error.WriteLine("AutoInit failed: " + ex.Message); }
    });
}

// ---------- 点选落盘：重命名 + 转 JPEG + 移动到指定目录 ----------
app.MapPost("/api/adopt/move", async (HttpRequest req) =>
{
    string? target = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("targetDir", out var td))
            target = td.GetString();
    }
    catch { /* 空或非法 body → 用默认目录 */ }
    if (string.IsNullOrWhiteSpace(target))
        target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DateTime.Now.ToString("yyyyMMddHHmmss"));
    target = Path.GetFullPath(target);
    Directory.CreateDirectory(target);

    var picks = LoadPicks();
    var items = new List<object>();
    var moved = 0;
    var skipped = 0;
    foreach (var (code, file) in picks)
    {
        string? src = null;
        foreach (var dir in config.CandidateDirs)
        {
            var fp = Path.Combine(dir, Path.GetFileName(file));
            if (File.Exists(fp)) { src = fp; break; }
        }
        if (src == null)
        {
            skipped++; items.Add(new { code, file, status = "skip", reason = "源文件不存在（可能已移动）" });
            continue;
        }
        var dst = Path.Combine(target, $"{code}.jpg");
        try
        {
            using var bmp = SKBitmap.Decode(src) ?? throw new InvalidOperationException("解码失败");
            using var img = bmp.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var fs = File.Create(dst);
            img.SaveTo(fs);
            File.Delete(src); // 移动：转换成功后删源
            moved++; items.Add(new { code, file = $"{code}.jpg", status = "ok", target = dst });
        }
        catch (Exception ex)
        {
            skipped++; items.Add(new { code, file, status = "error", reason = ex.Message });
        }
    }
    return J(new { targetDir = target, moved, skipped, items });
});

// 所选候选图相对其候选图目录的地址（找不到源文件时退回文件名）
string CandRelPath(string file)
{
    var safe = Path.GetFileName(file);
    foreach (var dir in config.CandidateDirs)
    {
        var fp = Path.Combine(dir, safe);
        if (File.Exists(fp))
            return Path.GetRelativePath(dir, fp).Replace(Path.DirectorySeparatorChar, '/');
    }
    return safe;
}

app.MapGet("/api/export", () =>
{
    try
    {
        var picks = LoadPicks();
        var sb = new StringBuilder("\ufeff");
        sb.Append("game_code,target_file\r\n");
        // 先按当前评分结果顺序，再补不在本次结果里的点选（配置/目录变过也不漏）
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Emit(string code)
        {
            if (!emitted.Add(code) || !picks.TryGetValue(code, out var file)) return;
            sb.Append(Q(code) + "," + Q(CandRelPath(file)) + "\r\n");
        }
        foreach (var g in games.EnumerateArray())
        {
            var code = g.GetProperty("code").GetString()!;
            if (picks.ContainsKey(code)) Emit(code);
        }
        foreach (var code in picks.Keys.Where(c => !emitted.Contains(c))
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            Emit(code);

        var name = $"已选图标-{DateTime.Now:yyyyMMddHHmmss}.csv";
        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", name);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return Results.Text("export failed: " + ex.Message, "text/plain; charset=utf-8", statusCode: 500);
    }
});

static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

app.Run();

class Config
{
    public List<string> CandidateDirs { get; set; } = new();
    public List<string> LiveDirs { get; set; } = new();
    [JsonConverter(typeof(PrefixListConverter))]
    public List<PrefixItem> LivePrefixes { get; set; } = new();
    public int DefaultPrefixIndex { get; set; }
    // 线上现图完整路径拼接规则，{code} 为游戏编码占位符
    public string LivePathPattern { get; set; } = "long/{code}.jpg";
    // 模型下载地址（发布包不带模型）
    public string ModelUrl { get; set; } = "";
    // OCR 模型基础地址（PP-OCRv5 目录）与识别模型预设（latin/ch/en）
    public string OcrModelUrl { get; set; } = "";
    public string OcrModelPreset { get; set; } = "latin";
    // 识别模型选择：预设键（latin/ch/en）或自定义基础地址（http…）
    public string OcrModel { get; set; } = "latin";
}

class ModelDownloadState
{
    public bool Running;
    public long Done, Total;
    public string? Error;
    public DateTime? StartedAt, FinishedAt;
}

class PrefixItem
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";

    public PrefixItem() { }
    public PrefixItem(string url, string name) { Url = url; Name = name; }
}

// 兼容两种持久化形态：旧版 string[]（纯 URL）与新版 [{url,name}]
class PrefixListConverter : JsonConverter<List<PrefixItem>>
{
    public override List<PrefixItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
        var list = new List<PrefixItem>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return list;
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    list.Add(new PrefixItem(reader.GetString() ?? "", ""));
                    break;
                case JsonTokenType.StartObject:
                    string url = "", name = "";
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName) continue;
                        var prop = reader.GetString();
                        reader.Read();
                        var val = reader.TokenType switch
                        {
                            JsonTokenType.String => reader.GetString() ?? "",
                            JsonTokenType.Number => reader.GetDouble().ToString("0.##"),
                            _ => "",
                        };
                        if (prop == "url") url = val ?? "";
                        else if (prop == "name") name = val ?? "";
                    }
                    if (!string.IsNullOrWhiteSpace(url)) list.Add(new PrefixItem(url, name));
                    break;
            }
        }
        throw new JsonException("prefixes 数组未闭合");
    }

    public override void Write(Utf8JsonWriter writer, List<PrefixItem> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var p in value)
        {
            writer.WriteStartObject();
            writer.WriteString("url", p.Url);
            writer.WriteString("name", p.Name);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
