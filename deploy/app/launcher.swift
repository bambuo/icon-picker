// icon-picker 桌面壳：启动内置后端，等待就绪后用原生窗口（WKWebView）展示工作台。
// 包内只有引擎（壳 + 后端 + 前端）；业务数据路径、模型地址、图标目录都由工作台「配置」管理，
// 运行期状态（config/picks/vectors/模型/评分结果）写在 ~/Library/Application Support/IconPicker。
import AppKit
import WebKit
import Foundation

final class SaveFileHandler: NSObject, WKScriptMessageHandler {
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any],
              let content = body["content"] as? String else { return }
        let name = (body["name"] as? String) ?? "export.csv"
        let fm = FileManager.default
        let dir = fm.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? fm.homeDirectoryForCurrentUser
        let url = dir.appendingPathComponent(name)
        try? content.data(using: .utf8)?.write(to: url)
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, WKNavigationDelegate, WKDownloadDelegate {
    private var window: NSWindow!
    private var webView: WKWebView!
    private var backend: Process?
    private var port = 0
    private var shuttingDown = false
    private var restarting = false
    private let saveFileHandler = SaveFileHandler()

    // MARK: - 生命周期

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        createWindow()
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            self?.boot()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        shuttingDown = true
        backend?.terminate()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { window?.makeKeyAndOrderFront(nil) }
        return true
    }

    // MARK: - 窗口

    private func createWindow() {
        let rect = NSRect(x: 0, y: 0, width: 1440, height: 900)
        window = NSWindow(contentRect: rect,
                          styleMask: [.titled, .closable, .miniaturizable, .resizable],
                          backing: .buffered, defer: false)
        window.title = "图标筛选工作台"
        window.minSize = NSSize(width: 1024, height: 700)
        window.isReleasedWhenClosed = false
        window.center()

        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = .default()
        cfg.userContentController.add(saveFileHandler, name: "saveFile")
        webView = WKWebView(frame: rect, configuration: cfg)
        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = self
        window.contentView = webView

        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        splash("正在启动…")
    }

    // MARK: - 运行期目录

    private var home: URL {
        if let custom = ProcessInfo.processInfo.environment["ICON_PICKER_HOME"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("IconPicker", isDirectory: true)
    }

    // MARK: - 启动流程

    private func boot() {
        let fm = FileManager.default
        guard let resources = Bundle.main.resourceURL else {
            showFatal("应用资源缺失。")
            return
        }
        let backendDir = resources.appendingPathComponent("backend")
        let exe = backendDir.appendingPathComponent("IconPicker.Api")
        guard fm.isExecutableFile(atPath: exe.path) else {
            showFatal("应用不完整：缺少后端程序。请重新安装。")
            return
        }

        splash("正在启动…")
        try? fm.createDirectory(at: home, withIntermediateDirectories: true)
        let dataDir = home.appendingPathComponent("data", isDirectory: true)
        try? fm.createDirectory(at: dataDir, withIntermediateDirectories: true)

        // 后端在用户目录里跑（内容根远离桌面等受保护路径，避免文件监听初始化被系统权限卡住）
        let runtimeDir = home.appendingPathComponent("runtime", isDirectory: true)
        try? fm.createDirectory(at: runtimeDir, withIntermediateDirectories: true)
        let srcWeb = backendDir.appendingPathComponent("wwwroot")
        let dstWeb = runtimeDir.appendingPathComponent("wwwroot")
        if fm.fileExists(atPath: srcWeb.path) {
            try? fm.removeItem(at: dstWeb)
            try? fm.copyItem(at: srcWeb, to: dstWeb)
        }
        for name in ["appsettings.json", "appsettings.Development.json"] {
            let src = backendDir.appendingPathComponent(name)
            let dst = runtimeDir.appendingPathComponent(name)
            if fm.fileExists(atPath: src.path) {
                try? fm.removeItem(at: dst)
                try? fm.copyItem(at: src, to: dst)
            }
        }

        port = freePort()
        guard port > 0 else {
            showFatal("找不到可用端口。")
            return
        }

        let logURL = home.appendingPathComponent("logs/backend.log")
        try? fm.createDirectory(at: home.appendingPathComponent("logs", isDirectory: true), withIntermediateDirectories: true)
        if !fm.fileExists(atPath: logURL.path) { fm.createFile(atPath: logURL.path, contents: nil) }
        let logHandle = try? FileHandle(forWritingTo: logURL)
        logHandle?.seekToEndOfFile()

        let proc = Process()
        proc.executableURL = exe
        proc.currentDirectoryURL = runtimeDir
        proc.arguments = ["--urls", "http://127.0.0.1:\(port)"]
        var env = ProcessInfo.processInfo.environment
        env["DATA_DIR"] = dataDir.path
        env["ASPNETCORE_ENVIRONMENT"] = "Production"
        env["ASPNETCORE_URLS"] = "http://127.0.0.1:\(port)"
        proc.environment = env
        if let h = logHandle {
            proc.standardOutput = h
            proc.standardError = h
        }
        proc.terminationHandler = { [weak self] p in
            guard let self = self, !self.shuttingDown, !self.restarting else { return }
            self.showFatal("后端已退出（code \(p.terminationStatus)）。日志：\(logURL.path)")
        }

        do {
            try proc.run()
        } catch {
            showFatal("无法启动后端：\(error.localizedDescription)")
            return
        }
        backend = proc
        try? String(port).write(to: home.appendingPathComponent("current-port.txt"), atomically: true, encoding: .utf8)

        splash("正在加载工作台…")
        if waitReady(timeout: 90) {
            DispatchQueue.main.async { [weak self] in
                guard let self = self else { return }
                self.webView.load(URLRequest(url: URL(string: "http://127.0.0.1:\(self.port)/")!))
            }
        } else if backend?.isRunning == true {
            showFatal("后端启动超时。日志：\(logURL.path)")
        }
    }

    private func restartBackend() {
        guard let old = backend else {
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in self?.boot() }
            return
        }
        restarting = true
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            old.terminate()
            var waited = 0.0
            while old.isRunning && waited < 10 {
                Thread.sleep(forTimeInterval: 0.1)
                waited += 0.1
            }
            self?.restarting = false
            self?.boot()
        }
    }

    private func waitReady(timeout: TimeInterval) -> Bool {
        let url = URL(string: "http://127.0.0.1:\(port)/api/scoring/status")!
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if backend?.isRunning != true { return false }
            let sem = DispatchSemaphore(value: 0)
            var ok = false
            session.dataTask(with: url) { _, resp, _ in
                if let http = resp as? HTTPURLResponse, http.statusCode == 200 { ok = true }
                sem.signal()
            }.resume()
            _ = sem.wait(timeout: .now() + 2)
            if ok { return true }
            Thread.sleep(forTimeInterval: 0.3)
        }
        return false
    }

    private func freePort() -> Int {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return 0 }
        defer { close(fd) }
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        let bound = withUnsafePointer(to: &addr) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bound == 0 else { return 0 }
        var out = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        let got = withUnsafeMutablePointer(to: &out) { p in
            p.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) }
        }
        guard got == 0 else { return 0 }
        return Int(UInt16(bigEndian: out.sin_port))
    }

    // MARK: - 提示页

    private func splash(_ message: String) {
        DispatchQueue.main.async { [weak self] in
            self?.webView.loadHTMLString(Self.page(message, spinning: true), baseURL: nil)
        }
    }

    private func showFatal(_ message: String) {
        DispatchQueue.main.async { [weak self] in
            self?.webView.loadHTMLString(Self.page(message, spinning: false), baseURL: nil)
        }
    }

    private static func page(_ message: String, spinning: Bool) -> String {
        let icon = spinning
            ? "<div class=\"dot\"></div>"
            : "<div class=\"mark\">!</div>"
        return """
        <!doctype html><html><head><meta charset="utf-8"><style>
        html,body{height:100%;margin:0;background:#0e1220;color:#e2e8f0}
        body{display:flex;align-items:center;justify-content:center;font:14px -apple-system,'PingFang SC',sans-serif}
        .box{text-align:center;max-width:680px;padding:0 24px}
        .dot{width:34px;height:34px;border:3px solid #27324d;border-top-color:#60a5fa;border-radius:50%;margin:0 auto 18px;animation:s 1s linear infinite}
        .mark{width:34px;height:34px;border-radius:50%;margin:0 auto 18px;background:#7f1d1d;color:#fecaca;font-weight:700;font-size:20px;line-height:34px}
        @keyframes s{to{transform:rotate(360deg)}}
        .msg{color:#cbd5e1;line-height:1.7;word-break:break-all}
        </style></head><body><div class="box">\(icon)<div class="msg">\(message)</div></div></body></html>
        """
    }

    // MARK: - 菜单

    private func buildMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "关于 图标筛选工作台", action: nil, keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏 图标筛选工作台", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 图标筛选工作台", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        main.addItem(editItem)

        let viewItem = NSMenuItem()
        let viewMenu = NSMenu(title: "显示")
        viewMenu.addItem(withTitle: "重新载入", action: #selector(reload(_:)), keyEquivalent: "r")
        viewItem.submenu = viewMenu
        main.addItem(viewItem)

        NSApp.mainMenu = main
    }

    @objc private func reload(_ sender: Any?) {
        if webView.url?.host == "127.0.0.1" { webView.reload() }
    }

    // MARK: - WKNavigationDelegate

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        // 导出 CSV 等附件下载
        if navigationAction.shouldPerformDownload {
            decisionHandler(.download)
            return
        }
        // 外部链接交给系统浏览器
        if let url = navigationAction.request.url,
           let host = url.host, host != "127.0.0.1" && host != "localhost",
           navigationAction.navigationType == .linkActivated {
            NSWorkspace.shared.open(url)
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse,
                  suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
        let dir = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
        completionHandler(dir.appendingPathComponent(suggestedFilename))
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
