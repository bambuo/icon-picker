using System.Text;
using System.Text.Json;
using IconPicker.Api;
using SkiaSharp;
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

// 数据根：DATA_ROOT env > appsettings DataRoot > 开发机目录（存在才用）> exe 相对 data-root
var root = Environment.GetEnvironmentVariable("DATA_ROOT")
    ?? app.Configuration["DataRoot"]
    ?? (Directory.Exists("/Users/johana/Desktop/新图标") ? "/Users/johana/Desktop/新图标" : Path.Combine(AppContext.BaseDirectory, "data-root"));
var dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
var vecPath = Path.Combine(dataDir, "vectors.json");
var picksPath = Path.Combine(dataDir, "picks.json");
var configPath = Path.Combine(dataDir, "config.json");
var resultsPath = Path.Combine(dataDir, "visual-results.json");

// ---------- config ----------
var config = new Config();
if (File.Exists(configPath))
{
    config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), JsonOpt()) ?? config;
}
else
{
    config.CandidateDirs.Add(Path.Combine(root, "未使用图标"));
    config.RealDirs.Add(Path.Combine(root, "真实图标-ppcabt"));
    config.RealDirs.Add(Path.Combine(root, "真实图标"));
    config.RealPrefixes.Add(new PrefixItem("https://static.ppcabt.com", "ppcabt"));
    config.RealPrefixes.Add(new PrefixItem("https://statictest.image-oss.online", "statictest"));
    config.DefaultPrefixIndex = 0;
    SaveConfig();
}
void SaveConfig() { Directory.CreateDirectory(dataDir); File.WriteAllText(configPath, JsonSerializer.Serialize(config, JsonOpt(true))); }
JsonSerializerOptions JsonOpt(bool indented = false) => new()
{
    WriteIndented = indented,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// ---------- 我方模板映射（code → 相对路径 / 名称 / 厅室） ----------
var templateIcon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var templateMeta = new Dictionary<string, (string Name, string Hall)>(StringComparer.OrdinalIgnoreCase);
string PtName(string raw)
{
    try
    {
        using var d = JsonDocument.Parse(raw);
        return d.RootElement.TryGetProperty("pt", out var v) ? v.GetString() ?? "" : "";
    }
    catch { return raw ?? ""; }
}
foreach (var f in Directory.EnumerateFiles(root, "t_game_template_*.json"))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        foreach (var e in doc.RootElement.GetProperty("t_game_template").EnumerateArray())
        {
            var code = e.GetProperty("game_code").GetString();
            if (string.IsNullOrEmpty(code)) continue;
            var icon = e.TryGetProperty("default_icon", out var di) ? di.GetString() : null;
            if (!string.IsNullOrEmpty(icon)) templateIcon.TryAdd(code, icon);
            templateMeta.TryAdd(code, (PtName(e.GetProperty("game_name").GetString() ?? ""), e.GetProperty("hall_code").GetString() ?? ""));
        }
    }
    catch { /* 忽略损坏的模板快照 */ }
}
string IconPathOf(string code) =>
    templateIcon.TryGetValue(code, out var p) ? p : $"/fec/game/icon/long/{Path.GetFileName(code)}.jpg";

// ---------- aa123 候选元数据（basename → 名称/厂商） ----------
var aaMeta = new Dictionary<string, (string Name, string Group)>(StringComparer.OrdinalIgnoreCase);
// aa123 与数据根为兄弟目录（…/Desktop/{新图标,aa123}），两级探测
var aaPath = new[] { Path.Combine(root, "aa123", "games.json"), Path.Combine(Path.GetDirectoryName(root) ?? "", "aa123", "games.json") }.FirstOrDefault(File.Exists) ?? "";
if (File.Exists(aaPath))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(aaPath));
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("games");
        foreach (var e in arr.EnumerateArray())
        {
            var baseName = Path.GetFileName(e.GetProperty("icon_file").GetString() ?? "");
            if (baseName.Length == 0) continue;
            aaMeta.TryAdd(baseName, (e.GetProperty("game_name").GetString() ?? "", e.GetProperty("group_name").GetString() ?? ""));
        }
    }
    catch { /* 忽略损坏快照 */ }
}

// ---------- 已采用集合（可选台账 CSV；无则不做排除） ----------
static List<string> ParseCsvLine(string line)
{
    var row = new List<string>(); var sb = new StringBuilder(); var q = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (q)
        {
            if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
            else sb.Append(c);
        }
        else if (c == '"') q = true;
        else if (c == ',') { row.Add(sb.ToString()); sb.Clear(); }
        else sb.Append(c);
    }
    row.Add(sb.ToString());
    return row;
}
var adoptedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var adoptedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var adoptedCsv = Path.Combine(root, "已采用图标清单.csv");
if (File.Exists(adoptedCsv))
{
    try
    {
        var lines = File.ReadAllLines(adoptedCsv);
        if (lines.Length > 1)
        {
            var head = ParseCsvLine(lines[0]);
            var iCode = head.IndexOf("game_code");
            var iSrc = head.IndexOf("aa123源图标");
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var row = ParseCsvLine(line);
                if (iCode >= 0 && iCode < row.Count) adoptedCodes.Add(row[iCode]);
                if (iSrc >= 0 && iSrc < row.Count) adoptedSources.Add(Path.GetFileName(row[iSrc]));
            }
        }
    }
    catch { /* 台账损坏时不做排除 */ }
}

// ---------- OCR 语料（basename → 文本） ----------
var ocrMap = new Dictionary<string, string>();
var ocrDir = Path.Combine(root, "icon-picker", "pipeline", "data", "ocr");
if (Directory.Exists(ocrDir))
{
    foreach (var f in Directory.EnumerateFiles(ocrDir, "*.jsonl"))
    {
        try
        {
            foreach (var line in File.ReadLines(f))
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var file = doc.RootElement.GetProperty("file").GetString() ?? "";
                var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
                ocrMap[Path.GetFileName(file)] = text ?? "";
            }
        }
        catch { /* 忽略损坏行 */ }
    }
}

// ---------- 数据与点选 ----------
JsonElement data = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(resultsPath));
JsonElement games = data.GetProperty("results");
var gameByCode = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
void LoadScoring(string json)
{
    data = JsonDocument.Parse(json).RootElement.Clone();
    games = data.GetProperty("results");
    gameByCode.Clear();
    foreach (var g in games.EnumerateArray())
        gameByCode[g.GetProperty("code").GetString()!] = g;
}
LoadScoring(File.ReadAllText(resultsPath));

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
    if (incoming.CandidateDirs.Count == 0) return J(new { error = "候选图目录不能为空" }, 400);
    incoming.RealPrefixes = incoming.RealPrefixes
        .Where(p => !string.IsNullOrWhiteSpace(p.Url))
        .Select(p => new PrefixItem(p.Url.Trim().TrimEnd('/'), string.IsNullOrWhiteSpace(p.Name) ? "未命名" : p.Name.Trim()))
        .DistinctBy(p => p.Url)
        .ToList();
    if (incoming.RealPrefixes.Count == 0) return J(new { error = "至少保留一个真实图前缀" }, 400);
    if (incoming.DefaultPrefixIndex < 0 || incoming.DefaultPrefixIndex >= incoming.RealPrefixes.Count)
        incoming.DefaultPrefixIndex = 0;
    // 目录地址原样持久化（允许先配置后建目录）；使用端按需跳过不存在的目录
    incoming.CandidateDirs = incoming.CandidateDirs.Select(NormDir).Distinct().ToList();
    incoming.RealDirs = incoming.RealDirs.Select(NormDir).Distinct().ToList();
    config = incoming;
    SaveConfig();
    return J(config);
});
string NormDir(string p) => Path.TrimEndingDirectorySeparator(Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(root, p)));

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
var modelPath = Path.Combine(app.Environment.ContentRootPath, "models", "vision_model.onnx");

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
}));

app.MapPost("/api/embed/rebuild", (HttpRequest req) =>
{
    if (!File.Exists(modelPath)) return J(new { error = "模型缺失：backend/models/vision_model.onnx" }, 400);
    if (ClipEmbedding.Running) return J(new { error = "重建已在进行中" }, 409);
    var force = bool.TryParse(req.Query["force"].ToString(), out var f) && f;
    Task.Run(() => ClipEmbedding.Rebuild(root, modelPath, force));
    return J(new { started = true, force });
});

// ---------- 评分模块数据源（评分在前端浏览器内执行） ----------
// ---------- 评分（后端计算，两阶段） ----------
app.MapPost("/api/scoring/compute", (HttpRequest req) =>
{
    if (ScoringService.Running) return J(new { error = "评分已在进行中" }, 409);
    var force = bool.TryParse(req.Query["force"].ToString(), out var f) && f;
    Task.Run(() => ScoringService.Compute(
        root,
        config.RealDirs.ToList().AsReadOnly(), config.CandidateDirs.ToList().AsReadOnly(),
        templateMeta, aaMeta, ocrMap,
        adoptedCodes, adoptedSources,
        vecPath, resultsPath, LoadScoring));
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

app.MapGet("/img/real/{*path}", async (string path) =>
{
    var name = Path.GetFileName(path);
    if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
    var safe = name + ".jpg";

    foreach (var dir in config.RealDirs)
    {
        var p = Path.Combine(dir, safe);
        if (File.Exists(p)) return Results.File(p, "image/jpeg");
    }

    // 本地未命中 → 依次尝试远程前缀（默认优先），成功后缓存到第一个真实图目录
    var prefixes = config.RealPrefixes.ToList();
    if (config.DefaultPrefixIndex > 0 && config.DefaultPrefixIndex < prefixes.Count)
        prefixes = [prefixes[config.DefaultPrefixIndex], .. prefixes.Where((_, i) => i != config.DefaultPrefixIndex)];
    var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("img");
    var iconPath = IconPathOf(name);
    foreach (var prefix in prefixes)
    {
        try
        {
            var url = prefix.Url.TrimEnd('/') + iconPath;
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) continue;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (config.RealDirs.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(config.RealDirs[0]);
                    await File.WriteAllBytesAsync(Path.Combine(config.RealDirs[0], safe), bytes);
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
if (app.Configuration.GetValue("AutoInit", true))
{
    Task.Run(() =>
    {
        try
        {
            if (!File.Exists(modelPath)) return;
            ClipEmbedding.Rebuild(root, modelPath, false); // 增量：已有键跳过
            ScoringService.Compute(
                root,
                config.RealDirs.ToList().AsReadOnly(), config.CandidateDirs.ToList().AsReadOnly(),
                templateMeta, aaMeta, ocrMap,
                adoptedCodes, adoptedSources,
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

app.MapGet("/api/export", () =>
{
    try
    {
        var picks = LoadPicks();
        var sb = new StringBuilder("\ufeff");
        sb.AppendLine("\"game_code\",\"hall\",\"name\",\"candidate_file\",\"aa123_group\",\"aa123_name\",\"total\",\"target_file\"");
        foreach (var g in games.EnumerateArray())
        {
            var code = g.GetProperty("code").GetString()!;
            if (!picks.TryGetValue(code, out var file)) continue;
            var cand = g.GetProperty("top").EnumerateArray()
                .FirstOrDefault(t => t.GetProperty("file").GetString() == file);
            string S(JsonElement e, string k) => e.ValueKind != JsonValueKind.Object ? ""
                : !e.TryGetProperty(k, out var v) ? ""
                : v.ValueKind == JsonValueKind.Number ? v.GetRawText()
                : v.GetString() ?? "";
            string Score(JsonElement e) => e.ValueKind != JsonValueKind.Object ? ""
                : !e.TryGetProperty("score", out var v) || v.ValueKind != JsonValueKind.Number ? "—"
                : v.GetDouble().ToString("0.000");
            // 过期点选：候选已不在当前 Top5 → 用 aa123 元数据补名称/厂商，分数留空
            string group, cname, score;
            if (cand.ValueKind == JsonValueKind.Object)
            {
                group = S(cand, "group"); cname = S(cand, "name"); score = Score(cand);
            }
            else
            {
                group = aaMeta.TryGetValue(Path.GetFileName(file), out var am) ? am.Group : "";
                cname = aaMeta.TryGetValue(Path.GetFileName(file), out var am2) ? am2.Name : "";
                score = "—";
            }
            sb.AppendLine(string.Join(",",
                Q(code), Q(S(g, "hall")), Q(S(g, "name")), Q(file),
                Q(group), Q(cname), Q(score),
                Q($"long/{code}.jpg")));
        }
        return Results.Text(sb.ToString(), "text/csv; charset=utf-8");
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
    public List<string> RealDirs { get; set; } = new();
    [JsonConverter(typeof(PrefixListConverter))]
    public List<PrefixItem> RealPrefixes { get; set; } = new();
    public int DefaultPrefixIndex { get; set; }
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
