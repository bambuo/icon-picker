using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace IconPicker.Api;

/// <summary>评分引擎：CLIP 余弦 + 名字/图上文字 + 像素同图（两阶段），结果落盘并回调热更新。</summary>
public static class ScoringService
{
    public static bool Running;
    public static string Phase = "";
    public static int Done, Total;
    public static string? Error;
    public static DateTime? StartedAt, FinishedAt;

    private const double SameImg = 0.78, High = 0.75, Mid = 0.62;
    private static readonly string[] TextBrand = ["pg", "soft", "pocket", "games", "premium", "gaming", "759"];
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp"];

    private static readonly Dictionary<string, double[]> VecNorm = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (double[] Gray, double[] Hist, ulong Dh)> PixCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex CamelSplit = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex Marks = new("[™®©]", RegexOptions.Compiled);

    private static string NormName(string s)
    {
        s = CamelSplit.Replace(s ?? "", "$1 $2").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return NonAlnum.Replace(Marks.Replace(sb.ToString(), " "), " ").Trim();
    }
    private static IEnumerable<string> Bigrams(string s)
    {
        var t = NormName(s).Replace(" ", "");
        for (var i = 0; i < t.Length - 1; i++) yield return t.Substring(i, 2);
    }
    private static double Dice(string a, string b)
    {
        var A = Bigrams(a).ToHashSet(); var B = Bigrams(b).ToHashSet();
        if (A.Count == 0 || B.Count == 0) return 0;
        var inter = A.Count(x => B.Contains(x));
        return 2.0 * inter / (A.Count + B.Count);
    }
    private static string NormText(string s) => NonAlnum.Replace(Marks.Replace(s ?? "", " ").ToLowerInvariant(), " ").Trim();
    private static string[] TextTokens(string s) =>
        NormText(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1 && !TextBrand.Contains(t)).ToArray();
    private static double? TextSim(string aText, string bText)
    {
        var A = TextTokens(aText); var B = TextTokens(bText).ToHashSet();
        if (A.Length == 0 || B.Count == 0) return null;
        var hit = A.Count(t => B.Contains(t));
        return 0.6 * (hit / (double)A.Length) + 0.4 * Dice(aText, bText);
    }
    private static double Combine(double img, double? text)
    {
        var num = 0.45 * img; var den = 0.45;
        if (text.HasValue) { num += 0.25 * text.Value; den += 0.25; }
        return num / den;
    }

    private static double[] NormVec(float[] raw)
    {
        var n = Math.Sqrt(raw.Sum(x => (double)x * x));
        return n > 0 ? raw.Select(x => x / n).ToArray() : raw.Select(_ => 0.0).ToArray();
    }
    private static double Cosine(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>像素三特征：32×32 灰度 + 直方图 + 9×8 dHash（SkiaSharp）。</summary>
    private static (double[] Gray, double[] Hist, ulong Dh) PixelFeatures(string file)
    {
        if (PixCache.TryGetValue(file, out var cached)) return cached;
        using var bmp = SKBitmap.Decode(file) ?? throw new InvalidOperationException($"decode failed: {file}");
        var gray = new double[32 * 32];
        var hist = new double[64];
        using (var resized = bmp.Resize(new SKImageInfo(32, 32), SKSamplingOptions.Default))
        {
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    var c = resized.GetPixel(x, y);
                    gray[y * 32 + x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                    hist[(c.Red >> 6) * 16 + (c.Green >> 6) * 4 + (c.Blue >> 6)]++;
                }
            for (var i = 0; i < 64; i++) hist[i] /= 32 * 32;
        }
        ulong dh = 0;
        using (var small = bmp.Resize(new SKImageInfo(9, 8), SKSamplingOptions.Default))
        {
            var g = new double[9 * 8];
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 9; x++)
                {
                    var c = small.GetPixel(x, y);
                    g[y * 9 + x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                }
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    dh = (dh << 1) | (g[y * 9 + x] > g[y * 9 + x + 1] ? 1ul : 0ul);
        }
        var result = (gray, hist, dh);
        PixCache[file] = result;
        return result;
    }
    private static double PixelSim((double[] Gray, double[] Hist, ulong Dh) a, (double[] Gray, double[] Hist, ulong Dh) b)
    {
        var corr = Pearson(a.Gray, b.Gray);
        ulong x = a.Dh ^ b.Dh; var ham = 0; while (x != 0) { ham += (int)(x & 1); x >>= 1; }
        double l1 = 0;
        for (var i = 0; i < 64; i++) l1 += Math.Abs(a.Hist[i] - b.Hist[i]);
        return 0.5 * ((corr + 1) / 2) + 0.3 * (1 - ham / 64.0) + 0.2 * Math.Max(0, 1 - l1 / 2);
    }
    private static double Pearson(double[] a, double[] b)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        var n = a.Length;
        for (var i = 0; i < n; i++) { sa += a[i]; sb += b[i]; saa += a[i] * a[i]; sbb += b[i] * b[i]; sab += a[i] * b[i]; }
        var den = Math.Sqrt((n * saa - sa * sa) * (n * sbb - sb * sb));
        return den == 0 ? 0 : (n * sab - sa * sb) / den;
    }

    /// <summary>两阶段评分：语义排序（CLIP+图上文字，秒级）→ 像素同图比对（后台）。onUpdate 回传最新 JSON。</summary>
    public static void Compute(
        IReadOnlyList<string> liveDirs, IReadOnlyList<string> candDirs,
        IReadOnlyDictionary<string, string> ocrByPath,
        string vectorsPath, string outPath, Action<string> onUpdate)
    {
        Running = true; Error = null; StartedAt = DateTime.Now; FinishedAt = null;
        try
        {
            if (!File.Exists(vectorsPath)) throw new InvalidOperationException("vectors.json 不存在，请先重建向量");
            var rawVec = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(vectorsPath)) ?? new();
            foreach (var (k, v) in rawVec) VecNorm[k] = NormVec(v);

            var liveFiles = liveDirs.Where(Directory.Exists).SelectMany(ListImages).ToList();
            var candFiles = candDirs.Where(Directory.Exists).SelectMany(ListImages).ToList();
            var lives = liveFiles.Select(f => (abs: f, code: Path.GetFileNameWithoutExtension(f), key: Path.GetFullPath(f))).ToList();
            var cands = candFiles.Select(f => (abs: f, baseName: Path.GetFileName(f), key: Path.GetFullPath(f))).ToList();
            Total = lives.Count; Done = 0; Phase = "语义排序";

            var results = new List<LiveResult>();
            foreach (var r in lives)
            {
                var qv = VecNorm.TryGetValue(r.key, out var v) ? v : Array.Empty<double>();
                var qtxt = ocrByPath.TryGetValue(r.abs, out var t) ? t : "";
                var scored = new List<(Cand c, double total)>();
                foreach (var p in cands)
                {
                    var img = VecNorm.TryGetValue(p.key, out var pv) && qv.Length > 0 ? Math.Clamp(Cosine(qv, pv), 0, 1) : 0;
                    var tx = TextSim(qtxt, ocrByPath.TryGetValue(p.abs, out var ot) ? ot : "");
                    var total = Combine(img, tx);
                    scored.Add((new Cand { File = p.baseName, Img = img, Text = tx }, total));
                }
                var top = scored.OrderByDescending(s => s.total).Take(5)
                    .Select(s => { s.c.Score = s.total; return s.c; }).ToList();
                results.Add(new LiveResult { ImageCode = r.code, Abs = r.abs, Top = top });
                Done++;
            }
            results.Sort((a, b) => (b.Top.FirstOrDefault()?.Img ?? -1).CompareTo(a.Top.FirstOrDefault()?.Img ?? -1));

            Phase = "落盘";
            var json1 = Serialize(results, cands.Count);
            File.WriteAllText(outPath, json1);
            onUpdate(json1);

            Phase = "像素同图比对"; Done = 0;
            var pixFiles = new List<string>();
            foreach (var r in results)
            {
                pixFiles.Add(r.Abs);
                pixFiles.AddRange(r.Top.Select(c => CandAbsOf(candDirs, c.File)));
            }
            pixFiles = pixFiles.Distinct().Where(File.Exists).ToList();
            Total = pixFiles.Count;
            foreach (var f in pixFiles)
            {
                try { PixelFeatures(f); } catch { /* 解码失败按无特征处理 */ }
                Done++;
            }
            foreach (var r in results)
            {
                var hasLiveFeat = PixCache.TryGetValue(r.Abs, out var rf);
                foreach (var c in r.Top)
                {
                    var candAbs = CandAbsOf(candDirs, c.File);
                    if (hasLiveFeat && PixCache.TryGetValue(candAbs, out var cf))
                    {
                        c.Pixel = PixelSim(rf, cf);
                        c.Corr = Pearson(rf.Gray, cf.Gray);
                    }
                }
            }
            var json2 = Serialize(results, cands.Count);
            File.WriteAllText(outPath, json2);
            onUpdate(json2);
            Phase = "完成";
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
        }
        finally
        {
            FinishedAt = DateTime.Now; Running = false;
        }
    }

    private static string Serialize(List<LiveResult> results, int candCount) =>
        JsonSerializer.Serialize(new
        {
            high = High, mid = Mid, sameImg = SameImg,
            poolCount = candCount,
            results = results.Select(r => new
            {
                imageCode = r.ImageCode, hasLive = true,
                top = r.Top.Select(c => new { file = c.File, score = c.Score, img = c.Img, pixel = c.Pixel, text = c.Text, corr = c.Corr }),
            }),
        });

    private static IEnumerable<string> ListImages(string dir) =>
        Directory.EnumerateFiles(dir).Where(f => ImageExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
    private static string CandAbsOf(IReadOnlyList<string> candDirs, string file)
    {
        foreach (var d in candDirs)
        {
            var p = Path.Combine(d, file);
            if (File.Exists(p)) return p;
        }
        return file;
    }

    public class Cand
    {
        public string File { get; set; } = "";
        public double Score { get; set; }
        public double Img { get; set; }
        public double? Pixel { get; set; }
        public double? Text { get; set; }
        public double? Corr { get; set; }
    }
    public class LiveResult
    {
        public string ImageCode { get; set; } = "";
        public string Abs { get; set; } = "";
        public List<Cand> Top { get; set; } = new();
    }
}
