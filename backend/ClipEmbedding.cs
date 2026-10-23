using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

/// <summary>CLIP ViT-B/32 视觉塔向量生成：SkiaSharp 预处理 + ONNX Runtime 推理。</summary>
public static class ClipEmbedding
{
    public static bool Running;
    public static int Done, Total;
    public static string? Error;
    public static DateTime? StartedAt, FinishedAt;

    private const int Side = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static InferenceSession? _session;
    private static readonly object SessionLock = new();

    private static InferenceSession Session(string modelPath)
    {
        if (_session != null) return _session;
        lock (SessionLock)
        {
            _session ??= new InferenceSession(modelPath);
            return _session;
        }
    }

    /// <summary>CLIP 预处理：短边缩放到 224 → 中心裁剪 → CHW 归一化。</summary>
    public static float[] Preprocess(string file)
    {
        using var bmp = SKBitmap.Decode(file) ?? throw new InvalidOperationException($"decode failed: {file}");
        var scale = (float)Side / Math.Min(bmp.Width, bmp.Height);
        var w = (int)Math.Round(bmp.Width * scale);
        var h = (int)Math.Round(bmp.Height * scale);
        using var resized = bmp.Resize(new SKImageInfo(w, h), SKSamplingOptions.Default);
        var x = (w - Side) / 2;
        var y = (h - Side) / 2;
        using var crop = new SKBitmap(Side, Side);
        using (var canvas = new SKCanvas(crop))
            canvas.DrawBitmap(resized, new SKRect(-x, -y, w - x, h - y));

        var bytes = crop.Bytes;
        var stride = crop.RowBytes;
        var area = Side * Side;
        var out3 = new float[3 * area];
        for (var yy = 0; yy < Side; yy++)
        {
            for (var xx = 0; xx < Side; xx++)
            {
                var idx = yy * stride + xx * 4; // BGRA
                var i = yy * Side + xx;
                out3[i] = (bytes[idx + 2] / 255f - Mean[0]) / Std[0];
                out3[area + i] = (bytes[idx + 1] / 255f - Mean[1]) / Std[1];
                out3[2 * area + i] = (bytes[idx] / 255f - Mean[2]) / Std[2];
            }
        }
        return out3;
    }

    /// <summary>推理单张 → L2 归一化向量（512 维）。</summary>
    public static float[] Embed(string modelPath, string file)
    {
        var session = Session(modelPath);
        var input = new DenseTensor<float>(Preprocess(file), [1, 3, Side, Side]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)]);
        var named = results.FirstOrDefault(o => o.Name == "image_embeds") ?? results.First();
        var tensor = named.AsTensor<float>();
        float[] vec = tensor.Dimensions.Length == 2
            ? tensor.ToArray()
            : Enumerable.Range(0, tensor.Dimensions[^1]).Select(i => tensor[0, 0, i]).ToArray();
        var n = MathF.Sqrt(vec.Sum(v => v * v));
        return n > 0 ? vec.Select(v => v / n).ToArray() : vec;
    }

    /// <summary>全量重建 vectors.json（默认跳过已有键，force 全部重算）。</summary>
    public static void Rebuild(string dataRoot, string modelPath, bool force)
    {
        Running = true; Error = null; Done = 0; StartedAt = DateTime.Now; FinishedAt = null;
        try
        {
            var backendData = Path.Combine(dataRoot, "icon-picker", "backend", "data");
            var cfgPath = Path.Combine(backendData, "config.json");
            var vecPath = Path.Combine(backendData, "vectors.json");
            var cfg = File.Exists(cfgPath)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath), JsonOpt) ?? new Config()
                : new Config();

            var jobs = new List<(string Key, string Abs)>();
            foreach (var d in cfg.CandidateDirs.Concat(cfg.RealDirs))
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp") continue;
                    jobs.Add((Path.GetRelativePath(dataRoot, f).Replace(Path.DirectorySeparatorChar, '/'), f));
                }
            }

            var vectors = File.Exists(vecPath)
                ? JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(vecPath), JsonOpt) ?? new()
                : new();
            var todo = jobs.Where(j => force || !vectors.TryGetValue(j.Key, out var v) || v.Length == 0).ToList();
            Total = todo.Count; Done = 0;
            foreach (var (key, abs) in todo)
            {
                vectors[key] = Embed(modelPath, abs).Select(v => MathF.Round(v, 5)).ToArray();
                Done++;
                if (Done % 200 == 0) Persist(vecPath, vectors);
            }
            Persist(vecPath, vectors);
            FinishedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
            FinishedAt = DateTime.Now;
        }
        finally { Running = false; }
    }

    private static void Persist(string path, Dictionary<string, float[]> vectors)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(vectors, JsonOpt));
        File.Move(tmp, path, true);
    }
}
