using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IconPicker.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace IconPicker.Desktop;

public sealed class MainWindow : Window
{
    private static readonly IBrush ChromeBg = new SolidColorBrush(Color.Parse("#0e1220"));
    private static readonly IBrush ChromeLine = new SolidColorBrush(Color.Parse("#1e293b"));
    private static readonly IBrush ChromeText = new SolidColorBrush(Color.Parse("#cbd5e1"));
    private static readonly IBrush HoverBg = new SolidColorBrush(Color.Parse("#1e293b"));
    private static readonly IBrush CloseHoverBg = new SolidColorBrush(Color.Parse("#dc2626"));

    private readonly TextBlock _status;
    private readonly Button _browserFallback;
    private readonly ContentControl _contentHost;
    private Grid _root = null!;
    private Border _rootBorder = null!;
    private Border _titleBar = null!;
    private TextBlock _minLabel = null!;
    private TextBlock _maxLabel = null!;
    private TextBlock _closeLabel = null!;
    private IBrush _hoverBg = HoverBg;
    private IBrush _closeHoverBg = CloseHoverBg;
    private readonly List<Control> _grips = new();
    private WebApplication? _host;
    private string? _url;

    public MainWindow()
    {
        Title = "图标筛选工作台";
        Width = 1440;
        Height = 900;
        MinWidth = 1024;
        MinHeight = 700;
        CanResize = true;
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("AppIcon.png"))
            if (icon is not null) Icon = new WindowIcon(icon);
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = ChromeBg;

        _status = new TextBlock
        {
            Text = "正在启动…",
            Foreground = Brushes.White,
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _browserFallback = new Button
        {
            Content = "在浏览器中打开",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _browserFallback.Click += (_, _) =>
        {
            if (_url is not null) OpenBrowser(_url);
        };

        _root = new Grid
        {
            Background = Brushes.Transparent,
            RowDefinitions = new RowDefinitions("36,*"),
        };
        _root.Children.Add(BuildTitleBar());

        _contentHost = new ContentControl { Margin = new Thickness(8, 0, 8, 8) };
        var contentRow = new Grid();
        contentRow.Children.Add(_contentHost);
        AddResizeGrips(contentRow);
        Grid.SetRow(contentRow, 1);
        _root.Children.Add(contentRow);

        _rootBorder = new Border
        {
            Background = ChromeBg,
            BorderBrush = ChromeLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _root,
        };
        Content = _rootBorder;
        _contentHost.Content = BuildSplash();

        Opened += async (_, _) => await BootAsync();
        Closed += (_, _) => StopAndExit();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateChrome();
        };
    }

    private Control BuildTitleBar()
    {
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };

        var drag = new Border { Background = Brushes.Transparent };
        drag.PointerPressed += OnTitleBarPressed;
        Grid.SetColumnSpan(drag, 2);
        bar.Children.Add(drag);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(CreateWindowButton("—", () => WindowState = WindowState.Minimized, out _minLabel));
        buttons.Children.Add(CreateWindowButton("□", ToggleMaximize, out _maxLabel));
        buttons.Children.Add(CreateWindowButton("×", Close, out _closeLabel, danger: true));
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);

        var topGrip = new Border
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        topGrip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(topGrip).Properties.IsLeftButtonPressed) Resize(WindowEdge.North, e);
        };
        _grips.Add(topGrip);
        bar.Children.Add(topGrip);

        _titleBar = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = ChromeLine,
            Child = bar,
        };
        return _titleBar;
    }

    private Border CreateWindowButton(string glyph, Action action, out TextBlock label, bool danger = false)
    {
        label = new TextBlock
        {
            Text = glyph,
            Foreground = ChromeText,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 46,
            Height = 36,
            Background = Brushes.Transparent,
            Child = label,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.PointerEntered += (_, _) => button.Background = danger ? _closeHoverBg : _hoverBg;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            action();
        };
        return button;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (WindowState == WindowState.Maximized) return;
        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception ex)
        {
            Log("拖动窗口失败：" + ex.Message);
        }
    }

    private void AddResizeGrips(Grid grid)
    {
        AddGrip(grid, WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, StandardCursorType.SizeWestEast);
        AddGrip(grid, WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, StandardCursorType.SizeWestEast);
        AddGrip(grid, WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, StandardCursorType.SizeNorthSouth);
        AddGrip(grid, WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, 12, 12, StandardCursorType.TopLeftCorner);
        AddGrip(grid, WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, 12, 12, StandardCursorType.TopRightCorner);
        AddGrip(grid, WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, 12, 12, StandardCursorType.BottomLeftCorner);
        AddGrip(grid, WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, 12, 12, StandardCursorType.BottomRightCorner);
    }

    private void AddGrip(Grid grid, WindowEdge edge, HorizontalAlignment ha, VerticalAlignment va,
        double width, double height, StandardCursorType cursor)
    {
        var grip = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            HorizontalAlignment = ha,
            VerticalAlignment = va,
            Cursor = new Cursor(cursor),
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) Resize(edge, e);
        };
        _grips.Add(grip);
        grid.Children.Add(grip);
    }

    private void Resize(WindowEdge edge, PointerPressedEventArgs e)
    {
        try
        {
            BeginResizeDrag(edge, e);
        }
        catch (Exception ex)
        {
            Log("缩放窗口失败：" + ex.Message);
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        _contentHost.Margin = maximized ? new Thickness(0) : new Thickness(8, 0, 8, 8);
        _rootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(8);
        _titleBar.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(8, 8, 0, 0);
        foreach (var grip in _grips) grip.IsVisible = !maximized;
        _maxLabel.Text = maximized ? "❐" : "□";
    }

    private Control BuildSplash()
    {
        return new Grid
        {
            Background = ChromeBg,
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { _status, _browserFallback },
                },
            },
        };
    }

    private async Task BootAsync()
    {
        try
        {
            Directory.CreateDirectory(DesktopPaths.Home);
            Directory.CreateDirectory(DesktopPaths.Logs);
            StartLog();

            var result = await Task.Run(async () =>
            {
                Payload.Extract();
                var host = IconPickerHost.Build(
                    Array.Empty<string>(),
                    new IconPickerHostOptions(
                        ContentRoot: DesktopPaths.Runtime,
                        WebRoot: Path.Combine(DesktopPaths.Runtime, "wwwroot"),
                        DataDir: DesktopPaths.Data,
                        Url: "http://127.0.0.1:0"));
                await host.StartAsync();
                return (host, url: UrlOf(host));
            });

            _host = result.host;
            _url = result.url;
            Log($"后端已就绪：{_url}");

            var web = new NativeWebView();
            web.WebMessageReceived += OnWebMessage;
            web.NavigationCompleted += async (_, _) =>
            {
                Log("页面加载完成");
                try { Log("页面标题：" + await web.InvokeScript("document.title")); }
                catch (Exception ex) { Log("读取页面标题失败：" + ex.Message); }
            };
            _contentHost.Content = web;
            web.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex);
            _status.Text = "启动失败：" + ex.Message;
            _browserFallback.IsVisible = _url is not null;
        }
    }

    private static string UrlOf(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var url = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? app.Urls.FirstOrDefault();
        return url ?? throw new InvalidOperationException("后端未返回监听地址");
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Body is null) return;
            using var doc = JsonDocument.Parse(e.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "theme")
            {
                var theme = ParseTheme(root);
                if (theme is not null) Dispatcher.UIThread.Post(() => ApplyTheme(theme));
                return;
            }
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (content is null) return;

            var fileName = SanitizeFileName(name);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log($"已保存导出文件：{path}");
            Reveal(path);
        }
        catch (Exception ex)
        {
            Log("保存导出文件失败：" + ex);
        }
    }

    private sealed record ChromeColors(Color Bg, Color Line, Color Text, Color Fill, Color Danger);

    private static ChromeColors? ParseTheme(JsonElement root)
    {
        var bg = ReadColor(root, "bg");
        var line = ReadColor(root, "line");
        var text = ReadColor(root, "text");
        var fill = ReadColor(root, "fill");
        if (bg is null || line is null || text is null || fill is null) return null;
        return new ChromeColors(
            bg.Value, line.Value, text.Value, fill.Value,
            ReadColor(root, "danger") ?? Color.FromRgb(245, 63, 63));
    }

    private static Color? ReadColor(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var values = arr.EnumerateArray().Take(4)
            .Select(v => v.TryGetDouble(out var d) ? (byte)Math.Clamp(Math.Round(d), 0, 255) : (byte)0)
            .ToArray();
        return values.Length switch
        {
            3 => Color.FromRgb(values[0], values[1], values[2]),
            4 => Color.FromArgb(values[3], values[0], values[1], values[2]),
            _ => null,
        };
    }

    private void ApplyTheme(ChromeColors colors)
    {
        Log($"窗口主题：bg={colors.Bg} line={colors.Line} text={colors.Text} fill={colors.Fill}");
        var bg = new SolidColorBrush(colors.Bg);
        var line = new SolidColorBrush(colors.Line);
        var text = new SolidColorBrush(colors.Text);
        _hoverBg = new SolidColorBrush(colors.Fill);
        _closeHoverBg = new SolidColorBrush(colors.Danger);

        TransparencyBackgroundFallback = bg;
        _rootBorder.Background = bg;
        _rootBorder.BorderBrush = line;
        _titleBar.Background = bg;
        _titleBar.BorderBrush = line;
        _minLabel.Foreground = text;
        _maxLabel.Foreground = text;
        _closeLabel.Foreground = text;
        _status.Foreground = text;
        _browserFallback.Foreground = text;
    }

    private static string SanitizeFileName(string? name)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "export.csv" : name);
        foreach (var ch in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "export.csv" : fileName;
    }

    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                StartProcess("open", "-R", path);
            else if (OperatingSystem.IsWindows())
                StartProcess("explorer.exe", $"/select,{path}");
            else
                StartProcess("xdg-open", Path.GetDirectoryName(path) ?? ".");
        }
        catch (Exception ex)
        {
            Log("打开下载目录失败：" + ex.Message);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开浏览器失败：" + ex.Message);
        }
    }

    private static void StartProcess(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    private void StopAndExit()
    {
        var host = _host;
        _host = null;

        _ = Task.Run(async () =>
        {
            if (host is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await host.StopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Log("停止后端失败：" + ex.Message);
                }
                try
                {
                    await host.DisposeAsync();
                }
                catch { }
            }
            Environment.Exit(0);
        });

        // 窗口已关闭；即使 Kestrel 因 WebView 连接挂住，也保证进程退出
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Environment.Exit(0);
        });
    }

    private static void StartLog()
    {
        var writer = new StreamWriter(DesktopPaths.LogFile, append: true) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
        Log($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 ===");
        Log($"home={DesktopPaths.Home}");
    }

    private static void Log(string message) => Console.WriteLine(message);
}
