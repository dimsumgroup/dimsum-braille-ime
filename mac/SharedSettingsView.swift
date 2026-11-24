// SharedSettingsView.swift — 管理 app 與報讀助手共用的設定界面（單一真相）
// 兩個進程（DimsumManager.app 與 DimsumVoiceHelper.app）各自編譯本檔，
// 界面完全一致：所有開關使用 Switch 樣式（WCAG 4.1.2 名稱/角色/值）。

import SwiftUI
import CoreGraphics

struct HelperShortcut {
    static let modifiers: CGEventFlags = [.maskCommand, .maskControl, .maskAlternate, .maskShift]
    private static let keys: [String: UInt16] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9,
        "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "o": 31, "u": 32,
        "i": 34, "p": 35, "l": 37, "j": 38, "'": 39, "k": 40, "n": 45, "m": 46, ".": 47
    ]
    private static let modifierNames: [(name: String, aliases: [String], flag: CGEventFlags)] = [
        ("Ctrl", ["ctrl", "control"], .maskControl), ("Option", ["option", "alt"], .maskAlternate),
        ("Cmd", ["cmd", "command"], .maskCommand), ("Shift", ["shift"], .maskShift)
    ]
    let keyCode: UInt16
    let flags: CGEventFlags

    init?(_ spec: String) {
        let parts = spec.split(separator: "+", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        guard parts.count >= 2, let key = parts.last, let code = Self.keys[key] else { return nil }
        var flags: CGEventFlags = []
        for part in parts.dropLast() {
            guard let modifier = Self.modifierNames.first(where: { $0.aliases.contains(part) }) else { return nil }
            flags.insert(modifier.flag)
        }
        self.keyCode = code
        self.flags = flags
    }

    func matches(_ key: Int64, _ held: CGEventFlags) -> Bool {
        key == Int64(keyCode) && held.intersection(Self.modifiers) == flags
    }

    static func label(for event: NSEvent) -> String? {
        let flags = CGEventFlags(rawValue: UInt64(event.modifierFlags.rawValue))
        let parts = modifierNames.filter { flags.contains($0.flag) }.map(\.name)
        guard !parts.isEmpty, let key = keys.first(where: { $0.value == event.keyCode })?.key else { return nil }
        return (parts + [key.uppercased()]).joined(separator: "+")
    }
}

// ============================================================================
// 報讀助手偏好設定（管理 app 內的視窗；與助手共用同一份設定檔，
// 修改後廣播通知令運行中的助手即時重新載入）
// ============================================================================
struct HelperConfigFile: Codable {
    static let defaultLookupHotkey = "Ctrl+Option+Cmd+'"
    static let defaultVerbosity = 1
    var ttsMode: Int = 0
    var speechRate: Float = 0.45
    var descriptionsEnabled: Bool = true
    var volume: Float = 1.0
    var verbosity: Int = Self.defaultVerbosity
    var settingsHotkey: String = ""
    var voiceModeHotkey: String = ""
    var readLineHotkey: String = ""
    var lookupHotkey: String = Self.defaultLookupHotkey
    var commitReadEnabled: Bool = false   // 上屏朗讀（規格：預設關閉）
    var navInterruptInterval: Double = 0.25
    var readWholeLineAfterCommit: Bool = false
    var loggingEnabled: Bool = true

    enum CodingKeys: String, CodingKey {
        case ttsMode, speechRate, descriptionsEnabled, volume, verbosity
        case settingsHotkey, voiceModeHotkey, readLineHotkey, lookupHotkey, commitReadEnabled, navInterruptInterval
        case readWholeLineAfterCommit
        case loggingEnabled
    }
    init() {}
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        ttsMode = try c.decodeIfPresent(Int.self, forKey: .ttsMode) ?? 0
        speechRate = try c.decodeIfPresent(Float.self, forKey: .speechRate) ?? 0.45
        descriptionsEnabled = try c.decodeIfPresent(Bool.self, forKey: .descriptionsEnabled) ?? true
        volume = try c.decodeIfPresent(Float.self, forKey: .volume) ?? 1.0
        verbosity = try c.decodeIfPresent(Int.self, forKey: .verbosity) ?? Self.defaultVerbosity
        if verbosity == 2 { verbosity = 1 } // 舊版詳細模式改用標準模式
        if !SharedSettingsNames.verbNames.indices.contains(verbosity) { verbosity = Self.defaultVerbosity }
        settingsHotkey = try c.decodeIfPresent(String.self, forKey: .settingsHotkey) ?? ""
        voiceModeHotkey = try c.decodeIfPresent(String.self, forKey: .voiceModeHotkey) ?? ""
        readLineHotkey = try c.decodeIfPresent(String.self, forKey: .readLineHotkey) ?? ""
        lookupHotkey = try c.decodeIfPresent(String.self, forKey: .lookupHotkey) ?? Self.defaultLookupHotkey
        commitReadEnabled = try c.decodeIfPresent(Bool.self, forKey: .commitReadEnabled) ?? false
        navInterruptInterval = try c.decodeIfPresent(Double.self, forKey: .navInterruptInterval) ?? 0.25
        readWholeLineAfterCommit = try c.decodeIfPresent(Bool.self, forKey: .readWholeLineAfterCommit) ?? false
        loggingEnabled = try c.decodeIfPresent(Bool.self, forKey: .loggingEnabled) ?? true
    }
}


/// 各界面共用的模式名稱
enum SharedSettingsNames {
    static let modeNames = ["Sin-ji 粵語語音", "系統語音", "VoiceOver 整合"]
    static let verbNames = ["精簡", "標準"]
}

struct HotkeyButtons: View {
    var text: Binding<String>
    var captureActive: Binding<Bool>
    var onChange: () -> Void
    var body: some View {
        HStack(spacing: 6) {
            Button("清除") { text.wrappedValue = ""; captureActive.wrappedValue = false; onChange() }
                .buttonStyle(.link).font(.caption)
            Button(captureActive.wrappedValue ? "按鍵盤按鍵…" : "點擊填入") { captureActive.wrappedValue.toggle() }
                .buttonStyle(.link).font(.caption)
        }
    }
}

/// 共用設定表單：管理 app 的「報讀助手設定」視窗與報讀助手選單列的
/// 「偏好設定…」視窗使用**完全相同**的此界面（用戶要求界面統一）。
struct HelperSettingsForm: View {
    @Binding var cfg: HelperConfigFile
    var onChange: () -> Void
    private var capturingFieldState = SwiftUI.State<String>(initialValue: "")
    private var capturingField: String {
        get { capturingFieldState.wrappedValue }
        nonmutating set { capturingFieldState.wrappedValue = newValue }
    }

    private func save() { onChange() }

    var body: some View {
        Form {
            Section("語音模式") {
                Picker("", selection: Binding(
                    get: { cfg.ttsMode },
                    set: { cfg.ttsMode = $0; save() })) {
                    ForEach(0..<3, id: \.self) { Text(SharedSettingsNames.modeNames[$0]).tag($0) }
                }.pickerStyle(.radioGroup).labelsHidden()
            }
            Section("朗讀內容") {
                Picker("", selection: Binding(
                    get: { cfg.verbosity },
                    set: { cfg.verbosity = $0; save() })) {
                    ForEach(SharedSettingsNames.verbNames.indices, id: \.self) { Text(SharedSettingsNames.verbNames[$0]).tag($0) }
                }.pickerStyle(.radioGroup).labelsHidden()
            }
            Section("語速與音量") {
                HStack {
                    Text("語速")
                    Slider(value: Binding(
                        get: { Double(cfg.speechRate) },
                        set: { cfg.speechRate = Float(($0 / 0.05).rounded() * 0.05); save() }),
                        in: 0.15...0.75)
                    Text("\(Int(cfg.speechRate * 100))%").monospacedDigit().frame(width: 44)
                }
                HStack {
                    Text("音量")
                    Slider(value: Binding(
                        get: { Double(cfg.volume) },
                        set: { cfg.volume = Float(($0 / 0.1).rounded() * 0.1); save() }),
                        in: 0.0...1.0)
                    Text("\(Int(cfg.volume * 100))%").monospacedDigit().frame(width: 44)
                }
            }
            Section("語音選項") {
                Toggle("候選用配詞報讀（例如「日」讀作「日子」）", isOn: Binding(
                    get: { cfg.descriptionsEnabled },
                    set: { cfg.descriptionsEnabled = $0; save() }))
                .toggleStyle(.switch)
                Toggle("上屏朗讀（預設關閉；開啟＝上屏時朗讀所選的字詞）", isOn: Binding(
                    get: { cfg.commitReadEnabled },
                    set: { cfg.commitReadEnabled = $0; save() }))
                .toggleStyle(.switch)
                Toggle("上屏後自動朗讀整行（預設關閉；開啟＝在上屏字詞之後也讀出整行）", isOn: Binding(
                    get: { cfg.readWholeLineAfterCommit },
                    set: { cfg.readWholeLineAfterCommit = $0; save() }))
                .toggleStyle(.switch)
            }
            Section("快捷鍵") {
                HStack {
                    TextField("偏好設定快捷鍵（留空＝停用）", text: Binding(
                        get: { cfg.settingsHotkey },
                        set: { cfg.settingsHotkey = $0.trimmingCharacters(in: .whitespaces); save() }))
                        .disabled(capturingField == "prefs")
                    HotkeyButtons(text: Binding(get: { cfg.settingsHotkey },
                        set: { cfg.settingsHotkey = $0.trimmingCharacters(in: .whitespaces); save() }),
                        captureActive: Binding(get: { capturingField == "prefs" },
                            set: { capturingField = $0 ? "prefs" : "" }),
                        onChange: { save() })
                }
                HStack {
                    TextField("語音設定模式快捷鍵（留空＝停用）", text: Binding(
                        get: { cfg.voiceModeHotkey },
                        set: { cfg.voiceModeHotkey = $0.trimmingCharacters(in: .whitespaces); save() }))
                        .disabled(capturingField == "voice")
                    HotkeyButtons(text: Binding(get: { cfg.voiceModeHotkey },
                        set: { cfg.voiceModeHotkey = $0.trimmingCharacters(in: .whitespaces); save() }),
                        captureActive: Binding(get: { capturingField == "voice" },
                            set: { capturingField = $0 ? "voice" : "" }),
                        onChange: { save() })
                }
                HStack {
                    TextField("朗讀整行快捷鍵（留空＝停用）", text: Binding(
                        get: { cfg.readLineHotkey },
                        set: { cfg.readLineHotkey = $0.trimmingCharacters(in: .whitespaces); save() }))
                        .disabled(capturingField == "readLine")
                    HotkeyButtons(text: Binding(get: { cfg.readLineHotkey },
                        set: { cfg.readLineHotkey = $0.trimmingCharacters(in: .whitespaces); save() }),
                        captureActive: Binding(get: { capturingField == "readLine" },
                            set: { capturingField = $0 ? "readLine" : "" }),
                        onChange: { save() })
                }
                HStack {
                    TextField("查詢最近報讀的配詞快捷鍵（留空＝停用）", text: Binding(
                        get: { cfg.lookupHotkey },
                        set: { cfg.lookupHotkey = $0.trimmingCharacters(in: .whitespaces); save() }))
                        .disabled(capturingField == "lookup")
                    HotkeyButtons(text: Binding(get: { cfg.lookupHotkey },
                        set: { cfg.lookupHotkey = $0.trimmingCharacters(in: .whitespaces); save() }),
                        captureActive: Binding(get: { capturingField == "lookup" },
                            set: { capturingField = $0 ? "lookup" : "" }),
                        onChange: { save() })
                }
                if capturingField != "" {
                    Text("正在欄位捕捉：請按下要設定的快捷鍵組合…").font(.caption).foregroundStyle(.orange)
                }
            }
            Section {
                HStack {
                    Button("語音測試") {
                        var voiceArg = ""
                        if cfg.ttsMode == 0 { voiceArg = "-v Sinji " }
                        let cmd = "if /usr/bin/say \(voiceArg)'測試' 2>/dev/null; then /usr/bin/say \(voiceArg)'語音測試。點心粵語點字輸入法'; else /usr/bin/say '語音測試。點心粵語點字輸入法'; fi"
                        let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/bash"); p.arguments = ["-c", cmd]
                        try? p.run()
                    }
                    Button("儲存設定") { save() }
                    Text("修改即時生效並自動儲存").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding()
        .formStyle(.grouped)
        .background(KeyCaptureLayer(
            active: capturingField != "",
            onCapture: { combo in
                switch capturingField {
                case "prefs": cfg.settingsHotkey = combo
                case "voice": cfg.voiceModeHotkey = combo
                case "readLine": cfg.readLineHotkey = combo
                case "lookup": cfg.lookupHotkey = combo
                default: break
                }
                capturingField = ""
                save()
            },
            onCancel: { capturingField = "" }))
    }
}

/// 在本視窗收到按鍵時記錄快捷鍵。
struct KeyCaptureLayer: NSViewRepresentable {
    var active: Bool
    var onCapture: (String) -> Void
    var onCancel: () -> Void
    func makeNSView(context: Context) -> NSView { NSView() }
    func updateNSView(_ v: NSView, context: Context) {
        if active { context.coordinator.start(in: v, onCapture: onCapture, cancel: onCancel) }
        else { context.coordinator.stop() }
    }
    static func dismantleNSView(_ view: NSView, coordinator: Coordinator) { coordinator.stop() }
    func makeCoordinator() -> Coordinator { Coordinator() }
    class Coordinator {
        private var monitor: Any?
        func start(in view: NSView, onCapture: @escaping (String) -> Void, cancel: @escaping () -> Void) {
            stop()
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak view] event in
                guard view?.window?.isKeyWindow == true else { return event }
                if event.keyCode == 53 { cancel() }
                else if let shortcut = HelperShortcut.label(for: event) {
                    onCapture(shortcut)
                }
                return nil
            }
        }
        func stop() {
            if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
        }
    }
}
