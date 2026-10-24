using System.Text.Json;
using RapidOcrNet;

namespace IconPicker.Api;

/// <summary>跨平台 OCR（PaddleOCR PP-OCRv5 ONNX，RapidOcrNet）：识别结果按文件绝对路径缓存到 data/ocr-cache.json。</summary>
public static class OcrService
{
    public static bool Running;
    public static int Done, Total;
    public static bool Downloading;
    public static int DlDone, DlTotal;
    public static string? DlError;
    public static DateTime? DlStartedAt, DlFinishedAt;
    public static string? Error;
    public static DateTime? StartedAt, FinishedAt;

    private static readonly object EngineLock = new();
    private static RapidOcr? _engine;
    private static string? _dataDir;
    private const string DetFile = "ch_PP-OCRv5_mobile_det.onnx";
    private const string DetRemote = "ch_PP-OCRv5_det_mobile.onnx";
    private const string ClsFile = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";
    private const string MetaFile = "model.json";

    /// <summary>支持的识别模型（检测/分类共用，识别与字典按语言选择）。</summary>
    public sealed record Preset(string RecRemote, string RecLocal, string DictUrl, string DictLocal);
    private static readonly Dictionary<string, Preset> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["latin"] = new("rec/latin_PP-OCRv5_rec_mobile.onnx", "latin_PP-OCRv5_rec_mobile_infer.onnx",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/latin_PP-OCRv5_rec_mobile/ppocrv5_latin_dict.txt",
            "ppocrv5_latin_dict.txt"),
        ["ch"] = new("rec/ch_PP-OCRv5_rec_mobile.onnx", "ch_PP-OCRv5_rec_mobile_infer.onnx",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt",
            "ppocrv5_dict.txt"),
        ["en"] = new("rec/en_PP-OCRv5_rec_mobile.onnx", "en_PP-OCRv5_rec_mobile_infer.onnx",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/en_PP-OCRv5_rec_mobile/ppocrv5_en_dict.txt",
            "ppocrv5_en_dict.txt"),
    };
    public static IReadOnlyCollection<string> PresetKeys => Presets.Keys;
    public static Preset ResolvePreset(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Presets.TryGetValue(key, out var p) ? p : Presets["latin"];

    private sealed class ModelMeta { public string Preset { get; set; } = "latin"; public string Rec { get; set; } = ""; public string Dict { get; set; } = ""; }

    public static void Configure(string dataDir)
    {
        _dataDir = dataDir;
        EnsureMeta(dataDir);
    }
    public static string ModelDir(string dataDir) => Path.Combine(dataDir, "ocr-models");
    private static string BundleV5() => Path.Combine(AppContext.BaseDirectory, "models", "v5");
    private static string? BundleDict()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", "ppocrv5_latin_dict.txt"),
            Path.Combine(BundleV5(), "ppocrv5_latin_dict.txt"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "v5", "ppocrv5_latin_dict.txt"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
    private static string? ResolveDict(string dir)
    {
        var p = Path.Combine(dir, "ppocrv5_latin_dict.txt");
        return File.Exists(p) ? p : BundleDict();
    }
    // 旧版下载只有拉丁文件、没有 model.json：补写元数据，避免升级后判定「未下载」
    private static void EnsureMeta(string dataDir)
    {
        var dir = ModelDir(dataDir);
        if (ReadMeta(dir) != null) return;
        var latin = ResolvePreset("latin");
        var dict = new[] { Path.Combine(dir, latin.DictLocal), BundleDict() ?? "" }.FirstOrDefault(File.Exists);
        if (File.Exists(Path.Combine(dir, DetFile)) && File.Exists(Path.Combine(dir, ClsFile))
            && File.Exists(Path.Combine(dir, latin.RecLocal)) && dict != null)
        {
            if (!File.Exists(Path.Combine(dir, latin.DictLocal)) && dict != null)
                try { File.Copy(dict, Path.Combine(dir, latin.DictLocal), true); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(dir, MetaFile),
                    JsonSerializer.Serialize(new ModelMeta { Preset = "latin", Rec = latin.RecLocal, Dict = latin.DictLocal }, JsonOpt));
            }
            catch { /* 只读目录忽略 */ }
        }
    }

    private static ModelMeta? ReadMeta(string dir)
    {
        var p = Path.Combine(dir, MetaFile);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<ModelMeta>(File.ReadAllText(p), JsonOpt) : null; }
        catch { return null; }
    }

    public static bool ModelsReady(string dataDir)
    {
        var meta = ReadMeta(ModelDir(dataDir));
        if (meta != null && File.Exists(Path.Combine(ModelDir(dataDir), DetFile))
            && File.Exists(Path.Combine(ModelDir(dataDir), ClsFile))
            && File.Exists(Path.Combine(ModelDir(dataDir), meta.Rec))
            && File.Exists(Path.Combine(ModelDir(dataDir), meta.Dict))) return true;
        foreach (var d in new[] { BundleV5(), Path.Combine(Directory.GetCurrentDirectory(), "models", "v5") })
            if (File.Exists(Path.Combine(d, DetFile)) && File.Exists(Path.Combine(d, ClsFile))
                && File.Exists(Path.Combine(d, ResolvePreset("latin").RecLocal)) && ResolveDict(d) != null) return true;
        return false;
    }
    public static string ActiveModelDir(string dataDir) => ModelsReady(dataDir) && ReadMeta(ModelDir(dataDir)) != null
        ? ModelDir(dataDir) : (Directory.Exists(BundleV5()) ? BundleV5() : ModelDir(dataDir));
    public static void ResetEngine()
    {
        lock (EngineLock) { _engine?.Dispose(); _engine = null; }
    }

    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp"];

    public sealed class Entry
    {
        public string Text { get; set; } = "";
        public long Size { get; set; }
        public long Mtime { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string CachePath(string dataDir) => Path.Combine(dataDir, "ocr-cache.json");

    public static Dictionary<string, Entry> LoadCache(string dataDir)
    {
        try
        {
            return File.Exists(CachePath(dataDir))
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath(dataDir))) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static Dictionary<string, string> LoadTexts(string dataDir) =>
        LoadCache(dataDir).ToDictionary(kv => kv.Key, kv => kv.Value.Text, StringComparer.OrdinalIgnoreCase);

    private static RapidOcr Engine()
    {
        lock (EngineLock)
        {
            _engine ??= CreateEngine();
            return _engine;
        }
    }

    private static RapidOcr CreateEngine()
    {
        var ocr = new RapidOcr();
        var dataDir = _dataDir ?? "";
        var dataMeta = ReadMeta(ModelDir(dataDir));
        string dir; string rec; string dict;
        if (dataMeta != null)
        {
            dir = ModelDir(dataDir);
            rec = dataMeta.Rec;
            dict = dataMeta.Dict;
        }
        else
        {
            dir = Directory.Exists(BundleV5()) ? BundleV5() : Path.Combine(Directory.GetCurrentDirectory(), "models", "v5");
            rec = ResolvePreset("latin").RecLocal;
            dict = ResolveDict(dir) ?? "";
        }
        if (File.Exists(Path.Combine(dir, DetFile)) && File.Exists(Path.Combine(dir, ClsFile))
            && File.Exists(Path.Combine(dir, rec)) && File.Exists(Path.Combine(dir, dict)))
        {
            ocr.InitModels(
                detPath: Path.Combine(dir, DetFile),
                clsPath: Path.Combine(dir, ClsFile),
                recPath: Path.Combine(dir, rec),
                keysPath: Path.Combine(dir, dict));
        }
        else
        {
            throw new InvalidOperationException("OCR 模型未下载：请在「配置 → 图上文字（OCR）」下载模型");
        }
        return ocr;
    }

    /// <summary>增量识别配置目录里的图片（force 全量）。每 100 张落盘一次。</summary>
    public static void Rebuild(IReadOnlyList<string> dirs, string dataDir, bool force, Action? onDone = null)
    {
        Running = true; Error = null; Done = 0; Total = 0; StartedAt = DateTime.Now; FinishedAt = null;
        try
        {
            var files = dirs.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d))
                .Where(f => ImageExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Total = files.Count;
            var cache = LoadCache(dataDir);
            var engine = Engine();
            foreach (var f in files)
            {
                try
                {
                    var info = new FileInfo(f);
                    if (!force && cache.TryGetValue(f, out var old)
                        && old.Size == info.Length && old.Mtime == info.LastWriteTimeUtc.Ticks)
                    {
                        Done++;
                        continue;
                    }
                    var res = engine.Detect(f, RapidOcrOptions.Default);
                    var text = (res.StrRes ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    cache[f] = new Entry { Text = text, Size = info.Length, Mtime = info.LastWriteTimeUtc.Ticks };
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"OCR failed {f}: {ex.Message}");
                }
                Done++;
                if (Done % 100 == 0) Persist(dataDir, cache);
            }
            Persist(dataDir, cache);
            FinishedAt = DateTime.Now;
            onDone?.Invoke();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            FinishedAt = DateTime.Now;
        }
        finally { Running = false; }
    }

    /// <summary>从基础地址下载 PP-OCRv5 检测/分类 + 指定语言的识别模型与字典。</summary>
    public static void DownloadModels(string baseUrl, string presetKey, string dataDir)
    {
        var preset = ResolvePreset(presetKey);
        Downloading = true; DlDone = 0; DlTotal = 4; DlError = null;
        DlStartedAt = DateTime.Now; DlFinishedAt = null;
        var dir = ModelDir(dataDir);
        try
        {
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; IconPicker/1.0)");

            void Fetch(string url, string local)
            {
                try
                {
                    using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    resp.EnsureSuccessStatusCode();
                    var dest = Path.Combine(dir, local);
                    var tmp = dest + ".tmp";
                    using (var src = resp.Content.ReadAsStream())
                    using (var fs = File.Create(tmp)) src.CopyTo(fs);
                    File.Move(tmp, dest, true);
                    DlDone++;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{url} -> {ex.Message}", ex);
                }
            }

            var b = baseUrl.TrimEnd('/');
            Fetch(b + "/det/" + DetRemote, DetFile);
            Fetch(b + "/cls/" + ClsFile, ClsFile);
            Fetch(b + "/" + preset.RecRemote, preset.RecLocal);
            Fetch(preset.DictUrl, preset.DictLocal);
            File.WriteAllText(Path.Combine(dir, MetaFile), JsonSerializer.Serialize(
                new ModelMeta { Preset = Presets.First(kv => kv.Value == preset).Key, Rec = preset.RecLocal, Dict = preset.DictLocal }, JsonOpt));
            ResetEngine();
            DlFinishedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            DlError = ex.Message;
            DlFinishedAt = DateTime.Now;
        }
        finally { Downloading = false; }
    }

    private static void Persist(string dataDir, Dictionary<string, Entry> cache)
    {
        Directory.CreateDirectory(dataDir);
        var path = CachePath(dataDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOpt));
        File.Move(tmp, path, true);
    }
}
