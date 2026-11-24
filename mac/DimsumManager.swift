// ============================================================================
// 點心粵語點字輸入法管理器 — macOS GUI (VoiceOver 無障礙)
// v2.5.2：報讀助手設定搬入管理 app（Cmd+` 切換）、組字朗讀含已完成字、首次權限引導
// v2.5：報讀助手 v2.0（VoiceOver 整合模式、差異朗讀、選單列圖示、可自訂快捷鍵）
// v2.4：DMG／隔離模式偵測與自動修復、報讀日誌修復、語音 fallback、無 CLT 相依
// ============================================================================
import SwiftUI
import Cocoa
import CoreServices

@main
struct DimsumManagerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    var body: some Scene {
        WindowGroup {
            ManagerView()
                .frame(minWidth: 620, idealWidth: 700, minHeight: 600, idealHeight: 880)
                .onAppear { NSApp.setActivationPolicy(.regular) }
        }
        // 報讀助手偏好設定：作為管理 app 的另一個視窗，
        // 與主視窗以 Cmd+` / Cmd+Tab 原生切換；修改即通知運行中的助手重新載入
        Window("報讀助手設定", id: "helperSettings") {
            HelperSettingsView()
                .frame(minWidth: 470, minHeight: 430)
        }
    }
}

class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ s: NSApplication) -> Bool { true }
}

// HelperConfigFile 已移至 SharedSettingsView.swift（管理 app 與報讀助手共用的單一真相）

enum HelperConfigStore {
    static var configPath: String {
        let dir = NSHomeDirectory() + "/Library/Application Support/DimsumVoiceOver"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        return dir + "/dimsum_vo_helper.json"
    }
    static func load() -> HelperConfigFile {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: configPath)),
              let cfg = try? JSONDecoder().decode(HelperConfigFile.self, from: data) else { return HelperConfigFile() }
        var c2 = cfg
        if c2.settingsHotkey == "Ctrl+Shift+Option+D" { c2.settingsHotkey = "" }
        return c2
    }
    static func save(_ cfg: HelperConfigFile) {
        if let data = try? JSONEncoder().encode(cfg) {
            try? data.write(to: URL(fileURLWithPath: configPath))
        }
        DistributedNotificationCenter.default().postNotificationName(
            Notification.Name("com.dimsum.braille.reload-config"),
            object: nil, userInfo: nil, deliverImmediately: true)
    }
}


struct HelperSettingsView: View {
    private var configState = SwiftUI.State<HelperConfigFile>(initialValue: HelperConfigStore.load())
    var body: some View {
        HelperSettingsForm(cfg: configState.projectedValue,
                           onChange: { HelperConfigStore.save(configState.wrappedValue) })
    }
}
// MARK: - Main View
struct ManagerView: View {
    @StateObject private var runner = ScriptRunner()
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 0) {
            headerView
            Divider()
            updateNotice
            ScrollView {
                VStack(spacing: 10) {
                    sectionTitle("首次安裝")
                    actionRow([
                        makeCard(runner.hasSquirrel ? "Squirrel 已安裝" : "01 安裝 Squirrel",
                                 "macOS 的 Rime 輸入法", "app.badge.checkmark", .teal,
                                 extraDisabled: runner.hasSquirrel) { runner.installSquirrel() },
                        makeCard(runner.hasDimsum ? "安裝／更新點心方案" : "02 安裝／更新點心方案",
                                 runner.hasSquirrel ? "一併部署候選報讀" : "先安裝 Squirrel",
                                 "square.and.arrow.down", .blue, extraDisabled: !runner.hasSquirrel) {
                            runner.run("install.sh", taskName: "安裝／更新點心方案")
                        },
                        makeCard(runner.helperRunning ? "報讀助手運行中"
                                 : runner.hasDimsum ? "03 啟動報讀助手" : "啟動報讀助手",
                                 runner.hasDimsum ? "首次啟動需授權" : "先安裝點心方案",
                                 "play.circle", .indigo, extraDisabled: !runner.hasDimsum || runner.helperRunning) {
                            runner.startHelper()
                        },
                    ])
                    sectionTitle("更新與維護")
                    actionRow([
                        makeCard("重新部署候選報讀", "供已安裝點心方案的用家", "arrow.triangle.2.circlepath", .blue) { runner.run("deploy-candidate-reporting.sh", taskName: "重新部署候選報讀") },
                        makeCard("更新字庫", "從 GitHub 取得最新字庫", "arrow.clockwise", .green,
                                 extraDisabled: !runner.hasDimsum || runner.isCheckingUpdates) { runner.updateDictionary() },
                        makeCard("更新發音", "取得最新 NVDA 字典並產生發音表", "waveform", .purple,
                                 extraDisabled: runner.isCheckingUpdates) { runner.updatePronunciations() },
                        makeCard("重啟輸入法", "重新載入 Squirrel", "arrow.triangle.2.circlepath", .yellow) { runner.restartSquirrel() },
                    ])
                    sectionTitle("語音報讀設定")
                    actionRow([
                        makeCard("報讀助手設定", "開啟偏好設定視窗", "slider.horizontal.3", .orange) { openWindow(id: "helperSettings") },
                        makeCard("測試語音", "測試粵語發音", "speaker.wave.2", .pink) { runner.testSpeech() },
                        makeCard("停止報讀", "停止語音助手", "stop.circle", .gray, extraDisabled: !runner.helperRunning, confirm: "確定要停止語音報讀助手嗎？") { runner.stopHelper() },
                    ])
                    actionRow([
                        makeCard("開機啟動", "登入自動啟動報讀", "power", .mint, extraDisabled: runner.autoStartEnabled) { runner.enableAutoStart() },
                        makeCard("關閉開機啟動", "取消自動啟動", "power.dashed", .secondary, extraDisabled: !runner.autoStartEnabled) { runner.disableAutoStart() },
                        makeCard("準備讀屏查配詞指令", "供 VoiceOver 和 TurnOver 指派快捷鍵", "keyboard", .blue) {
                            runner.installScreenReaderLookupActions()
                        },
                    ])
                    sectionTitle("日誌")
                    actionRow([
                        makeCard("開啟日誌", "查看報讀助手記錄", "doc.text.magnifyingglass", .cyan) { runner.openLog() },
                        makeCard("開啟記錄", "開始記錄日誌", "record.circle", .green, extraDisabled: runner.loggingEnabled) { runner.enableLogging() },
                        makeCard("關閉記錄", "停止記錄日誌", "record.circle.slash", .secondary, extraDisabled: !runner.loggingEnabled) { runner.disableLogging() },
                    ])
                    actionRow([
                        makeCard("清除日誌", "清空日誌內容", "eraser.fill", .orange, confirm: "確定要清除日誌內容嗎？\n（檔案保留、內容清空）") { runner.clearLog() },
                        makeCard("刪除日誌檔", "移除日誌檔案", "trash", .red, confirm: "確定要刪除日誌檔案嗎？\n（助手下次啟動時自動重建）") { runner.deleteLogFile() },
                    ])
                    sectionTitle("工具與設定")
                    actionRow([
                        makeCard("鍵盤設定", "系統輸入法設定", "keyboard", .orange) { runner.openKeyboardSettings() },
                        makeCard("Rime 設定", "開啟設定目錄", "folder", .brown) { runner.openRime() },
                        makeCard("系統狀態", "檢查所有元件", "checkmark.shield", .cyan) { runner.checkStatus() },
                        makeCard("版本資訊", "顯示安裝版本", "info.circle", .brown) { runner.showVersion() },
                    ])
                    actionRow([
                        makeCard("檢查更新", "檢查程式、字庫與發音資料", "arrow.triangle.up.circle", .blue,
                                 extraDisabled: runner.isCheckingUpdates) {
                            Task { await runner.checkForUpdates() }
                        },
                        makeCard("移除方案", "只移除點心輸入法", "trash", .red, confirm: "確定要移除點心方案嗎？\n\nSquirrel 輸入法引擎會保留。\n其他 Rime 方案不受影響。") { runner.run("uninstall.sh") },
                        makeCard("完全移除", "連 Squirrel 一起移除", "trash.fill", .red, confirm: "確定要完全移除嗎？\n\n這會同時移除：\n• 點心粵語輸入法方案\n• Squirrel 輸入法引擎\n\n此操作無法復原！") { runner.run("uninstall.sh", args: ["--full"]) },
                    ])
                }
                .padding(12)
            }
            Divider()
            outputPanel
            statusBar
        }
        .task {
            runner.refreshInstallationStatus()
            await runner.checkHelperAsync()
            await runner.checkAutoStartStatus()
            runner.loggingEnabled = HelperConfigStore.load().loggingEnabled   // 啟動時同步日誌開關狀態
            runner.performStartupChecks()
            await runner.checkForUpdatesOnLaunch()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            runner.refreshInstallationStatus()
            Task { await runner.checkHelperAsync() }
        }
        .alert("完成", isPresented: $runner.showSuccess) { Button("好") { } } message: { Text(runner.alertMsg) }
        .alert("錯誤", isPresented: $runner.showError) { Button("好") { } } message: { Text(runner.alertMsg) }
    }

    private var headerView: some View {
        HStack(spacing: 12) {
            Image(systemName: "character.bubble.fill").font(.system(size: 28)).foregroundStyle(.blue).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("點心粵語點字輸入法").font(.title3.bold())
                Text("管理器 V\(ScriptRunner.appVersion)").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 4) {
                Label(runner.helperRunning ? "報讀：運行中" : "報讀：已停止", systemImage: runner.helperRunning ? "speaker.wave.2.fill" : "speaker.slash.fill")
                    .font(.caption).foregroundStyle(runner.helperRunning ? .green : .secondary)
                Label(runner.autoStartEnabled ? "開機啟動：✓" : "開機啟動：✗", systemImage: runner.autoStartEnabled ? "power" : "power.dashed")
                    .font(.caption).foregroundStyle(runner.autoStartEnabled ? .mint : .secondary)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 10).background(.blue.opacity(0.05))
        .accessibilityElement(children: .contain)
    }

    @ViewBuilder private var updateNotice: some View {
        if runner.programUpdate != nil || runner.dictionaryUpdate != nil || runner.pronunciationUpdate != nil {
            VStack(alignment: .leading, spacing: 8) {
                Text("有更新可用").font(.headline).accessibilityAddTraits(.isHeader)
                if let release = runner.programUpdate {
                    HStack {
                        Text("程式 V\(release.tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV")))")
                        Spacer()
                        Link("查看新版程式", destination: release.page)
                    }
                }
                if let dictionary = runner.dictionaryUpdate {
                    HStack {
                        Text("字庫 \(dictionary.summary)")
                        Spacer()
                        Button("更新字庫") { runner.updateDictionary() }
                            .disabled(runner.isRunning || runner.isCheckingUpdates)
                    }
                }
                if let pronunciations = runner.pronunciationUpdate {
                    HStack {
                        Text("有新版發音資料（\(pronunciations.changedFiles.count) 個檔案）")
                        Spacer()
                        Button("更新發音") { runner.updatePronunciations() }
                            .disabled(runner.isRunning || runner.isCheckingUpdates)
                    }
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.blue.opacity(0.08))
            .accessibilityElement(children: .contain)
        }
    }

    @ViewBuilder private func sectionTitle(_ t: String) -> some View {
        HStack { Text(t).font(.headline); Spacer() }.padding(.top, 4).accessibilityAddTraits(.isHeader)
    }

    private func actionRow(_ cards: [ActionCard]) -> some View {
        HStack(spacing: 10) { ForEach(Array(cards.enumerated()), id: \.offset) { _, c in c }; Spacer(minLength: 0) }
            .accessibilityElement(children: .contain)
    }

    private func makeCard(_ title: String, _ subtitle: String, _ icon: String, _ color: Color,
                          extraDisabled: Bool = false, confirm: String? = nil, action: @escaping () -> Void) -> ActionCard {
        ActionCard(title: title, subtitle: subtitle, icon: icon, color: color,
                   disabled: extraDisabled || runner.isRunning, confirmMsg: confirm, action: action)
    }

    private var outputPanel: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text("操作輸出").font(.caption.bold()).foregroundStyle(.secondary)
                Spacer()
                outputHeaderButton(runner.showOutput ? "隱藏" : "顯示",
                    runner.showOutput ? "chevron.down" : "chevron.up") { withAnimation { runner.showOutput.toggle() } }
                if runner.showOutput {
                    outputHeaderButton("複製", "doc.on.doc") { runner.copyOutput() }
                    outputHeaderButton("清除", "trash") { runner.output = ""; runner.lastExitCode = -1 }
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
            if runner.showOutput {
                ScrollViewReader { proxy in
                    ScrollView {
                        Text(runner.output.isEmpty ? "（等待操作）" : runner.output)
                            .font(.system(size: 11, weight: .regular, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(8)
                            .textSelection(.enabled)
                            .id("output_end")
                            .accessibilityValue(runner.output.isEmpty ? "等待操作" : runner.output)
                    }
                    .background(Color(NSColor.textBackgroundColor))
                    .onChange(of: runner.output) { _ in
                        DispatchQueue.main.async {
                            withAnimation { proxy.scrollTo("output_end", anchor: .bottom) }
                        }
                    }
                }
                .frame(minHeight: 100, maxHeight: 260)
                .accessibilityElement(children: .contain)
                .accessibilityLabel("操作輸出").accessibilityHint("使用方向鍵逐行閱讀")
            }
        }
        .background(.bar.opacity(0.3))
    }

    private func outputHeaderButton(_ label: String, _ icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 3) {
                Image(systemName: icon).font(.system(size: 12, weight: .medium))
                Text(label).font(.system(size: 11, weight: .medium))
            }
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(.gray.opacity(0.12))
            .clipShape(RoundedRectangle(cornerRadius: 5))
            .contentShape(RoundedRectangle(cornerRadius: 5))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label).accessibilityHint("按 Enter 或空白鍵執行")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(.default) { action() }
    }

    private var statusBar: some View {
        HStack(spacing: 8) {
            if runner.isRunning {
                ProgressView().scaleEffect(0.6).frame(width: 14, height: 14).accessibilityHidden(true)
                Text(runner.currentTask).font(.caption).foregroundStyle(.secondary)
            } else {
                Circle().fill(runner.lastResultColor).frame(width: 8, height: 8).accessibilityHidden(true)
                Text(runner.lastResultText).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Text("腳本目錄：\(runner.scriptDirName)").font(.system(size: 9)).foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 12).padding(.vertical, 5).background(.bar)
        .accessibilityElement(children: .contain)
    }
}

// MARK: - Action Card
struct ActionCard: View {
    let title: String; let subtitle: String; let icon: String; let color: Color
    let disabled: Bool; let confirmMsg: String?; let action: () -> Void

    var body: some View {
        Button { handleAction() } label: {
            VStack(spacing: 3) {
                Image(systemName: icon).font(.system(size: 22)).foregroundStyle(disabled ? .gray : color).accessibilityHidden(true)
                Text(title).font(.caption.bold()).foregroundStyle(disabled ? .secondary : .primary)
                    .lineLimit(1).minimumScaleFactor(0.8)
                Text(subtitle).font(.system(size: 8)).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(width: 135, height: 72).background(.gray.opacity(0.08)).clipShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(CardPressStyle()).disabled(disabled)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(title).accessibilityValue(subtitle)
        .accessibilityHint(disabled ? "正在執行其他操作，請稍候" : "按 Enter 或空白鍵執行")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(.default) { handleAction() }
    }

    private func handleAction() {
        guard !disabled else { return }
        if let msg = confirmMsg {
            let a = NSAlert(); a.messageText = title; a.informativeText = msg; a.alertStyle = .warning
            a.addButton(withTitle: "確定"); a.addButton(withTitle: "取消")
            if a.runModal() == .alertFirstButtonReturn { action() }
        } else { action() }
    }
}

struct CardPressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.scaleEffect(configuration.isPressed ? 0.97 : 1.0).opacity(configuration.isPressed ? 0.85 : 1.0)
            .animation(.easeOut(duration: 0.1), value: configuration.isPressed)
    }
}

// MARK: - Output Text View
struct OutputTextView: NSViewRepresentable {
    let text: String
    func makeNSView(context: Context) -> NSScrollView {
        let sv = NSScrollView()
        let tv = NSTextView()
        tv.isEditable = false; tv.isSelectable = true
        tv.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        tv.backgroundColor = .textBackgroundColor; tv.textColor = .textColor
        tv.isVerticallyResizable = true; tv.isHorizontallyResizable = false
        tv.textContainer?.widthTracksTextView = true; tv.textContainerInset = NSSize(width: 8, height: 6)
        sv.documentView = tv; sv.hasVerticalScroller = true; sv.autohidesScrollers = false
        return sv
    }
    func updateNSView(_ nsView: NSScrollView, context: Context) {
        guard let tv = nsView.documentView as? NSTextView, tv.string != text else { return }
        tv.string = text
        tv.scrollRangeToVisible(NSRange(location: text.count, length: 0))
    }
}

// MARK: - Script Runner
@MainActor
class ScriptRunner: ObservableObject {
    @Published var output = ""
    @Published var isRunning = false
    @Published var currentTask = ""
    @Published var lastExitCode: Int32 = -1
    @Published var helperRunning = false
    @Published var autoStartEnabled = false
    @Published private(set) var hasSquirrel = false
    @Published private(set) var hasDimsum = false
    @Published private(set) var programUpdate: DimsumRelease?
    @Published private(set) var dictionaryUpdate: DimsumDictionaryUpdate?
    @Published private(set) var pronunciationUpdate: DimsumPronunciationUpdate?
    @Published private(set) var isCheckingUpdates = false
    @Published var loggingEnabled = HelperConfigStore.load().loggingEnabled   // 日誌記錄開關（config 持久化）
    @Published var showOutput = true
    @Published var showSuccess = false
    @Published var showError = false
    @Published var alertMsg = ""

    private var process: Process?
    private var foundScriptDir: String?
    private var checkedForUpdatesOnLaunch = false

    static let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.2.1"

    static let squirrelPath = "/Library/Input Methods/Squirrel.app"
    static let launchAgentLabel = "com.dimsum.vo-helper"
    static var launchAgentPlist: String { "\(NSHomeDirectory())/Library/LaunchAgents/com.dimsum.vo-helper.plist" }

    init() {}

    // MARK: - 路徑偵測
    func scriptDir() -> String {
        if let d = foundScriptDir { return d }
        if let ip = Bundle.main.resourcePath,
           FileManager.default.fileExists(atPath: "\(ip)/install.sh") {
            foundScriptDir = ip; return ip
        }
        let parent = (Bundle.main.bundlePath as NSString).deletingLastPathComponent
        if FileManager.default.fileExists(atPath: "\(parent)/install.sh") {
            foundScriptDir = parent; return parent
        }
        let grandparent = (parent as NSString).deletingLastPathComponent
        let macDir = "\(grandparent)/mac"
        if FileManager.default.fileExists(atPath: "\(macDir)/install.sh") {
            foundScriptDir = macDir; return macDir
        }
        foundScriptDir = parent; return parent
    }

    func repoDir() -> String? {
        let sd = scriptDir()
        let candidates = [
            "\(NSHomeDirectory())/Library/Application Support/DimsumManager/dimsum-braille-ime",
            "\(sd)/dimsum-braille-ime",
            "\((sd as NSString).deletingLastPathComponent)/dimsum-braille-ime",
        ]
        for c in candidates {
            let resolved = (c as NSString).standardizingPath
            if FileManager.default.fileExists(atPath: "\(resolved)/.git") { return resolved }
        }
        return nil
    }

    var scriptDirName: String { (scriptDir() as NSString).lastPathComponent }

    // MARK: - 狀態檢查
    private static var rimeDirectory: URL {
        URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
            .appendingPathComponent("Library/Rime", isDirectory: true)
    }

    private static var installedDictionaryURL: URL {
        rimeDirectory.appendingPathComponent(DimsumInstalledDictionary.filename)
    }

    private var pronunciationSources: DimsumPronunciationSources {
        DimsumPronunciationSources(
            bundledResources: URL(fileURLWithPath: scriptDir(), isDirectory: true),
            userOutput: URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)
                .appendingPathComponent("Library/Application Support/DimsumVoiceOver/voiceover_output", isDirectory: true)
        )
    }

    private static var installedSquirrelPath: String? {
        [squirrelPath, NSHomeDirectory() + squirrelPath].first {
            FileManager.default.isExecutableFile(atPath: "\($0)/Contents/MacOS/Squirrel")
        }
    }

    func refreshInstallationStatus() {
        hasSquirrel = Self.installedSquirrelPath != nil
        let required = ["dimsum_cb.schema.yaml", "dimsum_cb_std.schema.yaml", "dimsum_full.schema.yaml",
                        DimsumInstalledDictionary.filename, "build/dimsum_cb.table.bin"]
        hasDimsum = required.allSatisfy {
            FileManager.default.fileExists(atPath: Self.rimeDirectory.appendingPathComponent($0).path)
        }
        if !hasDimsum { dictionaryUpdate = nil }
    }

    func checkHelperAsync() async {
        let t = Task.detached(priority: .utility) {
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
            p.arguments = ["-x", "DimsumVoiceHelper"]
            let pipe = Pipe(); p.standardOutput = pipe; p.standardError = pipe
            do { try p.run(); p.waitUntilExit(); return p.terminationStatus == 0 } catch { return false }
        }
        helperRunning = await t.value
    }

    func checkAutoStartStatus() async {
        autoStartEnabled = FileManager.default.fileExists(atPath: Self.launchAgentPlist)
    }

    // MARK: - 啟動環境檢查（DMG 直執行／App Translocation／隔離屬性）
    func performStartupChecks() {
        let bundlePath = Bundle.main.bundlePath
        let isTranslocated = bundlePath.contains("AppTranslocation")
        let isOnVolume = bundlePath.hasPrefix("/Volumes/")
        if isTranslocated || isOnVolume {
            output += "⚠ 偵測到程式正從「\(isTranslocated ? "隔離暫存區" : "唯讀磁碟映像")」執行\n"
            output += "位置：\(bundlePath)\n\n"
            output += "此模式下「安裝／更新／開機啟動」等功能無法正常運作。\n"
            output += "請：\n  1. 關閉本程式\n  2. 將 DimsumManager.app 拖到「應用程式」資料夾\n  3. 再從「應用程式」開啟\n\n"
            showError = true
            alertMsg = isTranslocated
                ? "程式正在隔離暫存區執行\n\n請關閉程式，將它拖到「應用程式」資料夾後再開啟"
                : "程式正在 DMG 映像上執行\n\n請關閉程式，將它拖到「應用程式」資料夾後再開啟"
            return
        }
        let defaults = UserDefaults.standard
        if !defaults.bool(forKey: "DimsumInstallGuideShown") {
            defaults.set(true, forKey: "DimsumInstallGuideShown")
            output += "首次安裝請依次執行 01、02、03。已安裝 Squirrel 可跳過 01。\n"
            output += "第 02 步會一併部署候選報讀。首次啟動助手時，請允許「輔助使用」與「輸入監察」。\n\n"
        }
    }

    // MARK: - 腳本執行
    func run(_ script: String, args: [String] = [], taskName: String? = nil) {
        guard !isRunning else { return }
        let dir = scriptDir(); let path = "\(dir)/\(script)"
        guard FileManager.default.fileExists(atPath: path) else {
            output += "錯誤：找不到 \(script)\n搜尋路徑：\(dir)\n\n"
            showError = true; alertMsg = "找不到腳本：\(script)"; return
        }
        let label = taskName ?? script
        isRunning = true; currentTask = "\(label)…"; showOutput = true
        output += ">>> \(script) \(args.joined(separator: " "))\n\n"
        Task.detached(priority: .userInitiated) { [self] in
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = [path] + args; p.currentDirectoryURL = URL(fileURLWithPath: dir)
            let pipe = Pipe(); p.standardOutput = pipe; p.standardError = pipe
            let h = pipe.fileHandleForReading
            h.readabilityHandler = { hd in
                let d = hd.availableData; guard !d.isEmpty, let t = String(data: d, encoding: .utf8) else { return }
                Task { @MainActor in self.output += t }
            }
            p.terminationHandler = { proc in
                Task { @MainActor in
                    h.readabilityHandler = nil; self.lastExitCode = proc.terminationStatus
                    self.isRunning = false; self.currentTask = ""
                    if proc.terminationStatus == 0 {
                        self.output += "\n>>> 完成 ✓\n\n"
                        self.alertMsg = script == "update_accessibility.sh"
                            ? "更新發音完成。請重新啟動報讀助手以套用。" : "\(label) 完成"
                        self.showSuccess = true
                    } else {
                        self.output += "\n>>> 失敗 ✗（退出碼 \(proc.terminationStatus)）\n\n"
                        self.alertMsg = "\(label) 失敗"; self.showError = true
                    }
                    self.refreshInstallationStatus()
                    await self.checkHelperAsync()
                    if proc.terminationStatus == 0, script == "update_accessibility.sh",
                       let update = self.pronunciationUpdate,
                       self.pronunciationSources.appliedHashes() == update.sourceSHAs {
                        self.pronunciationUpdate = nil
                    }
                    if proc.terminationStatus == 0, ["install.sh", "update.sh", "update_accessibility.sh"].contains(script) {
                        await self.checkForUpdates(automatic: true)
                    }
                }
            }
            do { try p.run() } catch { Task { @MainActor in self.output += "錯誤：\(error)\n\n"; self.isRunning = false; self.showError = true; self.alertMsg = "啟動失敗" } }
        }
    }

    func runShell(_ command: String, taskName: String, workingDir: String? = nil) {
        guard !isRunning else { return }
        isRunning = true; currentTask = "\(taskName)…"; showOutput = true
        output += ">>> \(taskName)\n\n"
        Task.detached(priority: .userInitiated) { [self] in
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/bash"); p.arguments = ["-c", command]
            if let wd = workingDir { p.currentDirectoryURL = URL(fileURLWithPath: wd) }
            let pipe = Pipe(); p.standardOutput = pipe; p.standardError = pipe
            let h = pipe.fileHandleForReading
            h.readabilityHandler = { hd in
                let d = hd.availableData; guard !d.isEmpty, let t = String(data: d, encoding: .utf8) else { return }
                Task { @MainActor in self.output += t }
            }
            p.terminationHandler = { proc in
                Task { @MainActor in
                    h.readabilityHandler = nil; self.lastExitCode = proc.terminationStatus
                    self.isRunning = false; self.currentTask = ""
                    if proc.terminationStatus == 0 {
                        self.output += "\n>>> 完成 ✓\n\n"; self.alertMsg = "\(taskName) 完成"; self.showSuccess = true
                    } else {
                        self.output += "\n>>> 失敗 ✗（退出碼 \(proc.terminationStatus)）\n\n"
                        self.alertMsg = "\(taskName) 失敗"; self.showError = true
                    }
                    await self.checkHelperAsync()
                    await self.checkAutoStartStatus()
                }
            }
            do { try p.run() } catch { Task { @MainActor in self.output += "錯誤：\(error)\n\n"; self.isRunning = false; self.showError = true; self.alertMsg = "\(taskName) 失敗" } }
        }
    }

    // MARK: - Squirrel 安裝
    func installSquirrel() {
        showOutput = true
        refreshInstallationStatus()
        if hasSquirrel {
            output += "已安裝 Squirrel，可繼續第 02 步。\n\n"
            return
        }
        let installer = URL(fileURLWithPath: scriptDir()).appendingPathComponent("Squirrel-1.1.2.pkg")
        guard FileManager.default.fileExists(atPath: installer.path) else {
            output += "找不到 Squirrel 安裝包。\n\n"
            alertMsg = "找不到 Squirrel-1.1.2.pkg，請核對管理器打包內容。"
            showError = true
            return
        }
        if NSWorkspace.shared.open(installer) {
            output += "請在系統安裝程式完成 Squirrel 安裝，再回來執行第 02 步。\n"
            output += "如安裝程式提示重新登入，請先重新登入。\n\n"
        } else {
            alertMsg = "無法開啟 Squirrel 安裝程式"
            showError = true
        }
    }

    // MARK: - 報讀助手
    func installScreenReaderLookupActions() {
        let files = FileManager.default
        let resources = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers", isDirectory: true)
        let folder = files.homeDirectoryForCurrentUser
            .appendingPathComponent("Applications/點心查配詞", isDirectory: true)
        let staging = folder.appendingPathComponent(UUID().uuidString, isDirectory: true)
        showOutput = true
        output += "\n目前的管理器：\(Bundle.main.bundleURL.path)\n"
        output += "查配詞指令安裝位置：\(folder.path)\n"
        do {
            for reader in ScreenReaderLookup.allCases {
                let source = resources.appendingPathComponent(reader.bundleName, isDirectory: true)
                guard let sourceApp = Bundle(url: source), sourceApp.bundleIdentifier == reader.bundleID,
                      let executable = sourceApp.executableURL,
                      files.isExecutableFile(atPath: executable.path) else {
                    throw CocoaError(.fileReadCorruptFile, userInfo: [
                        NSLocalizedDescriptionKey: "目前開啟的管理器缺少可用的「\(reader.bundleName)」。請用 build-manager.sh 編譯，再開啟 build 裏的 DimsumManager.app。",
                        NSFilePathErrorKey: source.path,
                    ])
                }
                let dest = folder.appendingPathComponent(reader.bundleName, isDirectory: true)
                if files.fileExists(atPath: dest.path), Bundle(url: dest)?.bundleIdentifier != reader.bundleID {
                    throw CocoaError(.fileWriteFileExists, userInfo: [NSFilePathErrorKey: dest.path])
                }
            }
            try files.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? files.removeItem(at: staging) }
            for reader in ScreenReaderLookup.allCases {
                let source = resources.appendingPathComponent(reader.bundleName, isDirectory: true)
                let copy = staging.appendingPathComponent(reader.bundleName, isDirectory: true)
                let dest = folder.appendingPathComponent(reader.bundleName, isDirectory: true)
                try files.copyItem(at: source, to: copy)
                if files.fileExists(atPath: dest.path) {
                    _ = try files.replaceItemAt(dest, withItemAt: copy, backupItemName: nil)
                } else {
                    try files.moveItem(at: copy, to: dest)
                }
                output += "已複製：\(dest.lastPathComponent)\n"
            }

            let installedApps = ScreenReaderLookup.allCases.map { folder.appendingPathComponent($0.bundleName, isDirectory: true) }
            var registrationFailed = false
            for dest in installedApps {
                let result = LSRegisterURL(dest as CFURL, true)
                if result != noErr {
                    output += "macOS 未能登記「\(dest.lastPathComponent)」（錯誤 \(result)）。\n"
                    registrationFailed = true
                }
            }
            if registrationFailed {
                output += "兩個 App 已複製。可從 Finder 查看，或重新按「準備讀屏查配詞指令」嘗試登記。\n\n"
            } else {
                output += "請在 VoiceOver 或 TurnOver 的指令設定選擇對應的「點心查配詞」，再按讀屏鍵加撇號。\n\n"
            }
            NSWorkspace.shared.activateFileViewerSelecting(installedApps)
        } catch {
            output += "無法準備讀屏查配詞指令：\(error.localizedDescription)\n\n"
            alertMsg = "無法準備讀屏查配詞指令。"
            showError = true
        }
    }

    private var helperAppPath: String {
        let bundled = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/DimsumVoiceHelper.app").path
        if FileManager.default.fileExists(atPath: bundled) { return bundled }
        return "\(scriptDir())/DimsumVoiceHelper.app"
    }

    private func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    func startHelper() {
        let helperApp = helperAppPath
        guard FileManager.default.isExecutableFile(atPath: "\(helperApp)/Contents/MacOS/DimsumVoiceHelper") else {
            output += "錯誤：找不到報讀助手程式（DimsumVoiceHelper.app）\n"; showError = true; alertMsg = "找不到報讀助手程式"; return
        }
        output += "首次啟動請到「私隱與保安」，允許報讀助手的「輔助使用」與「輸入監察」，再重新啟動助手。\n\n"
        // 以 LaunchServices 啟動（open），助手成為獨立進程：
        // 「輔助使用」權限請求才會歸屬助手本身，授權清單才會正確出現「點心語音報讀助手」
        // --log：日誌寫到 ~/Library/Application Support/DimsumVoiceOver/，供「開啟日誌」使用
        runShell("open -g \(shellQuote(helperApp)) --args --log", taskName: "啟動報讀助手")
        Task { [self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            await checkHelperAsync()
            if !helperRunning {
                output += "⚠ 報讀助手未能啟動\n"
                output += "請確認「輔助使用」權限：系統設定 → 私隱與安全 → 輔助使用，\n"
                output += "把「點心語音報讀助手」的開關打開後再按一次「啟動報讀」。\n\n"
                showError = true
                alertMsg = "報讀助手未能啟動\n\n請到「系統設定 → 私隱與安全 → 輔助使用」\n把「點心語音報讀助手」開關打開後重試"
            }
        }
    }

    // MARK: - 報讀助手偏好設定（跨進程：啟動助手後以通知喚出其設定視窗）
    func showHelperPrefs() {
        let helperApp = helperAppPath
        let tool = "\(scriptDir())/dimsum_tool"
        guard FileManager.default.isExecutableFile(atPath: "\(helperApp)/Contents/MacOS/DimsumVoiceHelper"),
              FileManager.default.isExecutableFile(atPath: tool) else {
            output += "錯誤：找不到報讀助手或其工具程式\n"; showError = true; alertMsg = "找不到報讀助手程式"; return
        }
        // open：未執行則啟動（已執行則無動作）；通知由助手攔聽後開啟偏好設定視窗
        runShell("open -g \(shellQuote(helperApp)) --args --log 2>/dev/null; sleep 1.5; \(shellQuote(tool)) show-helper-prefs", taskName: "開啟報讀助手偏好設定")
    }

    func stopHelper() {
        runShell("pkill -f DimsumVoiceHelper 2>/dev/null; true", taskName: "停止報讀助手")
        Task { try? await Task.sleep(nanoseconds: 500_000_000); await checkHelperAsync() }
    }

    func testSpeech() {
        // Sin-ji 粵語語音未安裝時改用系統預設語音，並提示下載路徑
        runShell("if /usr/bin/say -v Sinji '測試' 2>/dev/null; then /usr/bin/say -v Sinji '點心粵語點字輸入法，語音測試'; else echo '⚠ 未安裝 Sinji 粵語語音，改用系統預設語音'; /usr/bin/say '點心粵語點字輸入法，語音測試'; echo '提示：到 系統設定 → 輔助功能 → 朗讀內容 → 聲音 下載「善怡 Sinji（中文－香港）」可獲得粵語報讀'; fi", taskName: "測試語音")
    }

    // MARK: - 開機自動啟動 (LaunchAgent)
    func enableAutoStart() {
        let helperPath = "\(helperAppPath)/Contents/MacOS/DimsumVoiceHelper"
        guard FileManager.default.isExecutableFile(atPath: helperPath) else {
            output += "錯誤：找不到報讀助手程式（DimsumVoiceHelper.app）\n"; showError = true; alertMsg = "找不到報讀助手程式"; return
        }
        let plistContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>\(Self.launchAgentLabel)</string>
            <key>ProgramArguments</key>
            <array>
                <string>\(helperPath)</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <false/>
        </dict>
        </plist>
        """
        do {
            try plistContent.write(toFile: Self.launchAgentPlist, atomically: true, encoding: .utf8)
            runShell("launchctl unload '\(Self.launchAgentPlist)' 2>/dev/null; launchctl load '\(Self.launchAgentPlist)' 2>&1", taskName: "設定開機自動啟動")
        } catch {
            output += "✗ 寫入 plist 失敗：\(error)\n\n"; showError = true; alertMsg = "建立開機啟動失敗"
        }
    }

    func disableAutoStart() {
        runShell("launchctl unload '\(Self.launchAgentPlist)' 2>/dev/null; rm -f '\(Self.launchAgentPlist)'; echo '已移除開機啟動設定'", taskName: "關閉開機自動啟動")
    }

    // MARK: - 程式、字庫及發音資料更新
    func checkForUpdatesOnLaunch() async {
        guard !checkedForUpdatesOnLaunch else { return }
        checkedForUpdatesOnLaunch = true
        await checkForUpdates(automatic: true)
    }

    func checkForUpdates(automatic: Bool = false) async {
        guard !isCheckingUpdates else { return }
        isCheckingUpdates = true
        defer { isCheckingUpdates = false }
        let oldProgram = programUpdate
        let oldDictionary = dictionaryUpdate
        let oldPronunciations = pronunciationUpdate
        let sources = pronunciationSources
        async let pronunciationResult = DimsumUpdates.checkPronunciations(sources)
        let (program, dictionary) = await DimsumUpdates.check(
            appVersion: Self.appVersion, dictionaryURL: Self.installedDictionaryURL
        )
        let pronunciations = await pronunciationResult
        guard !Task.isCancelled else {
            if automatic { checkedForUpdatesOnLaunch = false }
            return
        }

        var messages: [String] = []
        switch program {
        case .success(let release):
            programUpdate = release
            messages.append(release.map { "找到 Mac 程式更新 \($0.tag)" } ?? "沒有較新的正式 Mac 發行版")
        case .failure:
            messages.append("暫時無法檢查程式更新")
        }
        switch dictionary {
        case .success(let update):
            dictionaryUpdate = hasDimsum ? update : nil
            messages.append(update.map { "找到字庫更新 \($0.summary)" }
                ?? (hasDimsum ? "沒有較新的字庫" : "尚未安裝字庫，略過比較"))
        case .failure:
            messages.append("暫時無法檢查字庫更新")
        }

        switch pronunciations {
        case .success(.current):
            pronunciationUpdate = nil
            messages.append("發音資料已是最新")
        case .success(.unrecorded):
            pronunciationUpdate = nil
            messages.append("未記錄本機發音資料的來源版本，可按「更新發音」建立紀錄")
        case .success(.update(let update)):
            pronunciationUpdate = update
            messages.append("找到新版發音資料：\(update.changedFiles.joined(separator: "、"))")
        case .failure:
            messages.append("暫時無法檢查發音資料更新")
        }

        if !automatic {
            showOutput = true
            output += ">>> 檢查更新\n" + messages.joined(separator: "\n") + "\n\n"
        }
        var announcements: [String] = []
        if let programUpdate, programUpdate != oldProgram {
            announcements.append("有新版程式")
        }
        if let dictionaryUpdate, dictionaryUpdate != oldDictionary {
            announcements.append("有新版字庫")
        }
        if let pronunciationUpdate, pronunciationUpdate != oldPronunciations {
            announcements.append("有新版發音資料")
        }
        if !announcements.isEmpty {
            NSAccessibility.post(
                element: NSApp as Any,
                notification: NSAccessibility.Notification(rawValue: "AXAnnouncement"),
                userInfo: [
                    NSAccessibility.NotificationUserInfoKey(rawValue: "AXAnnouncement"): announcements.joined(separator: "。"),
                    NSAccessibility.NotificationUserInfoKey(rawValue: "AXPriority"): 50,
                ]
            )
        }
    }

    func updateDictionary() {
        guard hasDimsum, !isCheckingUpdates else { return }
        run("update.sh", args: ["--pull"], taskName: "更新字庫")
    }

    func updatePronunciations() {
        guard !isCheckingUpdates else { return }
        run("update_accessibility.sh", args: ["--pull"], taskName: "更新發音")
    }

    // MARK: - 版本資訊
    func showVersion() {
        output += "═══ 版本資訊 ═══\n\n"
        output += "點心粵語點字輸入法\n"
        output += "發行團隊：Dimsum Group\n"
        output += "程式版本：V\(Self.appVersion)\n"
        output += "macOS：\(ProcessInfo.processInfo.operatingSystemVersionString)\n\n"
        if let squirrel = Self.installedSquirrelPath {
            if let dict = NSDictionary(contentsOfFile: "\(squirrel)/Contents/Info.plist"),
               let ver = dict["CFBundleShortVersionString"] as? String {
                output += "Squirrel：v\(ver)\n"
            } else {
                output += "Squirrel：已安裝（版本無法讀取）\n"
            }
        } else { output += "Squirrel：未安裝\n" }
        refreshInstallationStatus()
        output += "點心方案：\(hasDimsum ? "已安裝" : "未安裝")\n"
        let dictionary = DimsumInstalledDictionary(at: Self.installedDictionaryURL)
        output += "本機已部署字庫：\(dictionary?.summary ?? "未安裝或無法讀取")\n"
        output += "\n── 報讀助手 ──\n"
        output += "狀態：\(helperRunning ? "運行中" : "已停止")\n"
        output += "開機啟動：\(autoStartEnabled ? "已啟用" : "未啟用")\n"
        let dir = scriptDir()
        let json = "\(dir)/voiceover_output/DimsumPronunciations.json"
        if FileManager.default.fileExists(atPath: json),
           let data = try? Data(contentsOf: URL(fileURLWithPath: json)),
           let arr = try? JSONDecoder().decode([String: String].self, from: data) {
            output += "發音資料：\(arr.count) 條\n"
        }
        if let repo = repoDir() {
            output += "\n── 本地 Git 快取 ──\n"
            let cmd = "cd \(shellQuote(repo)) && git log -1 --format='提交：%H%n日期：%ar%n說明：%s'"
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/bash"); p.arguments = ["-c", cmd]
            let pipe = Pipe(); p.standardOutput = pipe; p.standardError = Pipe()
            if (try? p.run()) != nil { p.waitUntilExit(); if let s = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) { output += "\n\(s)\n" } }
        }
        output += "\n═══ 版本資訊結束 ═══\n\n"
        showOutput = true
    }

    // MARK: - 開啟日誌
    func openLog() {
        let configDir = "\(NSHomeDirectory())/Library/Application Support/DimsumVoiceOver"
        let logPath = "\(configDir)/dimsum_vo_helper.log"
        let altLogPath = "\(scriptDir())/config/dimsum_vo_helper.log"
        if FileManager.default.fileExists(atPath: logPath) {
            NSWorkspace.shared.open(URL(fileURLWithPath: logPath)); output += "已開啟日誌：\(logPath)\n\n"
        } else if FileManager.default.fileExists(atPath: altLogPath) {
            NSWorkspace.shared.open(URL(fileURLWithPath: altLogPath)); output += "已開啟日誌：\(altLogPath)\n\n"
        } else {
            output += "找不到日誌檔案\n報讀助手可能尚未產生日誌\n\n"
        }
    }

    // MARK: - 日誌管理（清除／刪除／開啟／關閉記錄）
    static let helperLogPath = "\(NSHomeDirectory())/Library/Application Support/DimsumVoiceOver/dimsum_vo_helper.log"

    /// 清除日誌內容（檔案保留、清成空檔；助手下次寫入由檔頭繼續）
    func clearLog() {
        let p = Self.helperLogPath
        guard FileManager.default.fileExists(atPath: p) else {
            output += "日誌檔案不存在（無需清除）\n\n"; return
        }
        let ok = FileManager.default.createFile(atPath: p, contents: Data())   // 覆寫為空檔
        if ok {
            output += "✓ 日誌內容已清除：\(p)\n\n"
            alertMsg = "日誌內容已清除"; showSuccess = true
        } else {
            output += "✗ 清除日誌失敗\n"; showError = true; alertMsg = "清除日誌失敗"
        }
    }

    /// 刪除日誌檔案（助手下次啟動或「開啟日誌記錄」時會自動重建）
    func deleteLogFile() {
        let p = Self.helperLogPath
        guard FileManager.default.fileExists(atPath: p) else {
            output += "日誌檔案不存在（無需刪除）\n\n"; return
        }
        do {
            try FileManager.default.removeItem(atPath: p)
            output += "✓ 日誌檔案已刪除：\(p)\n（助手下次啟動或「開啟日誌記錄」時會自動重建）\n\n"
            alertMsg = "日誌檔案已刪除"; showSuccess = true
        } catch {
            output += "✗ 刪除日誌失敗：\(error.localizedDescription)\n"
            showError = true; alertMsg = "刪除日誌失敗"
        }
    }

    /// 開啟／關閉日誌記錄：寫入設定檔（持久化）＋廣播 reload-config 令運行中的助手即時生效
    func setLogging(_ on: Bool) {
        var cfg = HelperConfigStore.load()
        cfg.loggingEnabled = on
        HelperConfigStore.save(cfg)   // save() 內建廣播 com.dimsum.braille.reload-config
        loggingEnabled = on
        if on {
            output += "✓ 日誌記錄：已開啟（立即生效；重啟助手後仍保持開啟）\n\n"
            alertMsg = "已開啟日誌記錄"; showSuccess = true
        } else {
            output += "✓ 日誌記錄：已關閉（立即生效；重啟助手後仍保持關閉；已產生的日誌檔保留）\n\n"
            alertMsg = "已關閉日誌記錄"; showSuccess = true
        }
    }
    func enableLogging() { setLogging(true) }
    func disableLogging() { setLogging(false) }

    // MARK: - 其他工具
    func openRime() {
        let path = "\(NSHomeDirectory())/Library/Rime"
        if FileManager.default.fileExists(atPath: path) {
            NSWorkspace.shared.open(URL(fileURLWithPath: path)); output += "已開啟 Rime 設定目錄\n\n"
        } else { output += "Rime 設定目錄不存在\n請先安裝輸入法\n\n"; showError = true; alertMsg = "Rime 設定目錄不存在" }
    }

    func restartSquirrel() {
        runShell("if [ -d '/Library/Input Methods/Squirrel.app' ]; then pkill -f Squirrel 2>/dev/null; sleep 1; open '/Library/Input Methods/Squirrel.app' 2>/dev/null; echo '✓ Squirrel 已重啟'; else echo '✗ 未安裝 Squirrel，請先按「安裝 Squirrel」'; exit 1; fi", taskName: "重啟 Squirrel")
    }

    func openKeyboardSettings() {
        if #available(macOS 13.0, *) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Keyboard")!)
            output += "已開啟系統設定 → 鍵盤\n請在「輸入法」分頁加入「Squirrel」\n\n"
        } else {
            NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Library/PreferencePanes/Keyboard.prefPane"))
            output += "已開啟系統偏好設定 → 鍵盤\n\n"
        }
    }

    // MARK: - 系統狀態
    func checkStatus() {
        output += "═══ 系統狀態檢查 ═══\n\n"
        let dir = scriptDir(); let rd = "\(NSHomeDirectory())/Library/Rime"
        let squirrel = Self.installedSquirrelPath != nil
        output += "Squirrel 輸入法：\(squirrel ? "✓ 已安裝" : "✗ 未安裝")\n"
        let schema = FileManager.default.fileExists(atPath: "\(rd)/dimsum_cb.schema.yaml")
        output += "Rime 方案：\(schema ? "✓ 已安裝" : "✗ 未安裝")\n"
        let dict = FileManager.default.fileExists(atPath: "\(rd)/build/dimsum_cb.table.bin")
        output += "字典編譯：\(dict ? "✓ 已完成" : "✗ 未完成")\n"
        let builtSchema = try? String(contentsOfFile: "\(rd)/build/dimsum_cb.schema.yaml", encoding: .utf8)
        let candidateReporter = builtSchema?.contains("lua_processor@*dimsum_candidate_reporter") == true
            && FileManager.default.fileExists(atPath: "\(rd)/lua/dimsum_candidate_reporter.lua")
        output += "候選報讀元件：\(candidateReporter ? "✓ 已部署" : "✗ 未部署，可執行「重新部署候選報讀」")\n"
        output += "報讀助手：\(helperRunning ? "✓ 運行中" : "○ 已停止")\n"
        output += "開機啟動：\(autoStartEnabled ? "✓ 已設定" : "○ 未設定")\n"
        let json = FileManager.default.fileExists(atPath: "\(dir)/voiceover_output/DimsumPronunciations.json")
        output += "發音資料：\(json ? "✓ 已產生" : "✗ 未產生")\n"
        if let repo = repoDir() { output += "GitHub 儲存庫：✓ \(repo)\n" }
        else { output += "GitHub 儲存庫：✗ 找不到\n" }
        let ok = squirrel && schema && dict
        output += "\n\(ok ? "整體狀態：✓ 輸入法已就緒" : "整體狀態：⚠ 需要安裝或更新")\n\n═══ 檢查完成 ═══\n\n"
        showOutput = true; lastExitCode = ok ? 0 : 1
    }

    func copyOutput() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(output, forType: .string)
        output += "[已複製到剪貼板]\n"
    }

    var lastResultColor: Color {
        if isRunning { return .blue }
        if lastExitCode == 0 { return .green }
        if lastExitCode == -1 { return .gray }
        return .red
    }
    var lastResultText: String {
        if isRunning { return "執行中…" }
        if lastExitCode == 0 { return "就緒 ✓" }
        if lastExitCode == -1 { return "等待操作" }
        return "錯誤 ✗（\(lastExitCode)）"
    }
}
