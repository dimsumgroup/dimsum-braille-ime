import Cocoa
import SwiftUI
import Carbon
import AVFoundation
import Darwin
import ApplicationServices

// ============================================================================
// 點心語音報讀助手 v2.0（DimsumVoiceHelper）
//
// 朗讀模式（ttsMode）：
//   0 = Sin-ji 粵語語音（助手獨立語音）
//   1 = 系統語音（say）
//   2 = VoiceOver 整合（朗讀交由 VoiceOver，使用其語音與設定）
//
// 朗讀內容：精簡讀候選，標準在每個候選前加選字數字。
//
// 設定模式快捷鍵可自訂（config JSON：settingsHotkey，預設 Ctrl+Shift+Option+D），
// 也可從選單列「點」圖示開啟。
//
// 朗讀一律採用「差異朗讀」：只朗讀文字框內新變化的部分，
// 不再重讀整行舊內容（修復重複朗讀 Bug）。
// ============================================================================

// ============================================================================
// 【取樣診斷工具】--probe-inject：由助手自注點字鍵序並錄下 AX 原始值。
// 助手已取得「輔助使用＋輸入監控」授權，由它注入鍵事件不受 TCC 歸屬問題影響。
// 用途：為語音規格第二步（穩固切割）收集真實的組字區／候選窗原文。
// 用法：DimsumVoiceHelper --probe-inject　（完成後自動退出，寫入 /tmp/probe_report.log）
// ============================================================================

private func probeWriteLog(_ text: String) {
    let p = "/tmp/probe_report.log"
    if !FileManager.default.fileExists(atPath: p) {
        try? "=== probe_report ===\n".write(toFile: p, atomically: true, encoding: .utf8)
    }
    if let h = FileHandle(forWritingAtPath: p) {
        h.seekToEndOfFile()
        h.write(text.data(using: .utf8) ?? Data())
        h.closeFile()
    }
}

// 點字鍵位（與 helper 事件監聽一致）：f=3 d=2 s=1 j=38 k=40 l=37
private let probeKeyCodes: [String: CGKeyCode] = ["f": 3, "d": 2, "s": 1, "j": 38, "k": 40, "l": 37]
// 【並擊鍵組→碼字全表（2026-11-17 依 schema chord_composer 的 xform 規則完整重建）】
// 鍵＝並擊鍵組（brailleLetter 轉字母後 ASCII 排序串接，與 finalizeBrailleChord 同構），
// 值＝Rime 實際收到的碼字。涵蓋全部 xform 規則，包括舊版遺漏的：
//   • fs→k（唯一規則，整條遺漏——實錄「請呃」的 k 並擊被靜默丟失，碼 xNa≠Rime 實收 xNka）
//   • a→a（單鍵 a 無 xform，原樣輸出）
//   • af 系列大寫別鍵組（af→A、afd→B、afj→C…——與非 af 規則同碼字，
//     舊版「碼字→鍵組」一對一反建，同碼別鍵組全部被撞掉丟失）
// 鍵一律為 ASCII 排序（a<d<f<j<k<l<s）。
// 【段錯誤教訓】此表必須是「直接字面量全域」——不可用 `let X = 另一全域` 的
// 拷貝式初始化（本次 `let chordSetMap = probeChordMap` 令惰性初始化序列
// 產生 NULL 儲存槽 → --self-test 存取即 EXC_BAD_ACCESS at 0x10）。
let chordSetMap: [String: String] = [
    // 單鍵（7）
    "a": "a", "d": "A", "f": "a", "j": "@", "k": "\"", "l": ",", "s": "'",
    // 兩鍵（16）
    "af": "A", "df": "b", "dj": "i", "dk": "C", "dl": "E", "ds": "B",
    "fj": "c", "fk": "e", "fl": "*", "fs": "k",
    "jk": "^", "jl": ".", "js": "/", "kl": ";", "ks": "K", "ls": "-",
    // 三鍵（25）
    "adj": "I", "adf": "B", "afj": "C", "afk": "E", "afs": "K",
    "dfj": "f", "dfk": "H", "dfl": "<", "dfs": "l",
    "djl": "[", "djk": "j", "djs": "s", "dkl": "D", "dks": "F", "dls": "J",
    "fjk": "d", "fjl": "%", "fjs": "m", "fkl": ":", "fks": "o", "fls": "u",
    "jkl": "_", "jks": ">", "jls": "+", "kls": "N",
    // 四鍵（24）
    "adfj": "F", "adfk": "H", "adjk": "J", "adfs": "L", "adjs": "S",
    "dfjk": "G", "dfjl": "$", "dfjs": "p", "dfkl": "\\", "dfks": "r", "dfls": "v",
    "djkl": "w", "djks": "t", "djls": "!", "dkls": "I",
    "afjk": "D", "afjs": "M", "afks": "O", "afls": "U",
    "fjkl": "?", "fjks": "n", "fjls": "x", "fkls": "z", "jkls": "#",
    // 五鍵以上（17）
    "adfjk": "G", "adfjs": "P", "adfjks": "Q", "adfks": "R", "adfls": "V",
    "adjks": "T", "adjkl": "W",
    "afjks": "N", "afjls": "X", "afjkls": "Y", "afkls": "Z",
    "dfjkl": "]", "dfjks": "q", "dfjls": "&", "dfjkls": "=", "dfkls": "(",
    "fjkls": "y", "djkls": ")"
]

private func probePump(_ seconds: Double) {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline {
        RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.02))
    }
}

private func probePostKey(_ code: CGKeyCode, _ down: Bool) {
    let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)
    ev?.post(tap: .cghidEventTap)
    probePump(0.003)
}

private func probeChord(_ keys: [CGKeyCode]) {
    for k in keys {
        probePostKey(k, true)
        probePump(0.035)
    }
    probePump(0.130)
    for k in keys.reversed() {
        probePostKey(k, false)
        probePump(0.030)
    }
    probePump(0.180)
}

private func probeTapKey(_ code: CGKeyCode) {
    probePostKey(code, true)
    probePump(0.060)
    probePostKey(code, false)
}

// 輪詢 Squirrel 候選窗（與 daemon 的 getSquirrelCandidateText 同邏輯）
private func probeSquirrelWindows() -> String {
    for app in NSWorkspace.shared.runningApplications {
        let isSq = (app.bundleIdentifier == "im.rime.inputmethod.Squirrel")
            || (app.localizedName ?? "").contains("Squirrel")
        if !isSq { continue }
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        var winsRef: CFTypeRef?
        AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &winsRef)
        if let wins = winsRef as? [AXUIElement], !wins.isEmpty {
            var texts: [String] = []
            for w in wins {
                var kidsRef: CFTypeRef?
                AXUIElementCopyAttributeValue(w, kAXChildrenAttribute as CFString, &kidsRef)
                if let kids = kidsRef as? [AXUIElement] {
                    for c in kids {
                        var v: CFTypeRef?
                        AXUIElementCopyAttributeValue(c, kAXValueAttribute as CFString, &v)
                        if let s = v as? String, !s.isEmpty { texts.append(s) }
                        var gRef: CFTypeRef?
                        AXUIElementCopyAttributeValue(c, kAXChildrenAttribute as CFString, &gRef)
                        if let gs = gRef as? [AXUIElement] {
                            for g in gs {
                                var gv: CFTypeRef?
                                AXUIElementCopyAttributeValue(g, kAXValueAttribute as CFString, &gv)
                                if let s = gv as? String, !s.isEmpty { texts.append(s) }
                            }
                        }
                    }
                }
            }
            return texts.joined(separator: " | ")
        }
    }
    return ""
}

// 選取（並自動啟用）Squirrel 輸入源；呼叫方負責記住先前輸入源以便還原
private func probeSelectSquirrel() -> Bool {
    let list = TISCreateInputSourceList(nil, false)?.takeRetainedValue() as? [TISInputSource] ?? []
    for s in list {
        let idStr = probeSourceID(s)
        if idStr.contains("Squirrel") {
            let e = TISEnableInputSource(s)
            print("PROBE: TISEnable(\(idStr)) = \(e)")
            fflush(stdout)
            let r = TISSelectInputSource(s)
            print("PROBE: TISSelect(\(idStr)) = \(r)")
            fflush(stdout)
            probePump(0.6)
            return r == noErr
        }
    }
    print("PROBE: 清單中找不到 Squirrel 輸入源")
    fflush(stdout)
    return false
}

// 取輸入源 ID（nil 安全）
private func probeSourceID(_ src: TISInputSource?) -> String {
    guard let src = src else { return "" }
    let idPtr = TISGetInputSourceProperty(src, kTISPropertyInputSourceID)
    return idPtr != nil ? (Unmanaged<CFString>.fromOpaque(idPtr!).takeUnretainedValue() as String) : ""
}

// 【AX 面板可達性實驗】--ax-scrape-pid <pid>：跨進程抓取指定 app 的 windows／深度 AX 樹，
// 用於驗證 shielding-level nonactivating NSPanel 在外部進程的可見性與文字所在屬性；
// 實驗結果寫 /tmp/ax_scrape_report.log 後退出。
private func probeAXScrapePID() {
    let args = CommandLine.arguments
    guard let idx = args.firstIndex(of: "--ax-scrape-pid"), idx + 1 < args.count,
          let pid = pid_t(args[idx + 1]) else { exit(2) }
    let axApp = AXUIElementCreateApplication(pid)
    var out = "=== AX scrape pid=\(pid) ===\n"

    func getStr(_ el: AXUIElement, _ attr: String) -> String {
        var v: CFTypeRef?
        AXUIElementCopyAttributeValue(el, attr as CFString, &v)
        return (v as? String) ?? ""
    }
    func getEls(_ el: AXUIElement, _ attr: String) -> [AXUIElement] {
        var v: CFTypeRef?
        AXUIElementCopyAttributeValue(el, attr as CFString, &v)
        return (v as? [AXUIElement]) ?? []
    }
    func dump(_ el: AXUIElement, depth: Int, out: NSMutableString, maxLevel: Int) {
        if depth > maxLevel { return }
        let role = getStr(el, kAXRoleAttribute as String)
        var val = getStr(el, kAXValueAttribute as String)
        if val.count > 400 { val = String(val.prefix(400)) + "…" }
        out.append(String(repeating: "  ", count: depth) + "[\(depth)] role=\(role.isEmpty ? "?" : role)")
        if !val.isEmpty { out.append(" AXValue=\"\(val)\"") }
        out.append("\n")
        for child in getEls(el, kAXChildrenAttribute as String) {
            dump(child, depth: depth + 1, out: out, maxLevel: maxLevel)
        }
    }
    let buf = NSMutableString()
    for attr in [kAXWindowsAttribute as String, kAXFocusedWindowAttribute as String, kAXChildrenAttribute as String] {
        var v: CFTypeRef?
        let err = AXUIElementCopyAttributeValue(axApp, attr as CFString, &v)
        out.append("app.\(attr) err=\(err)\n")
        if let els = v as? [AXUIElement] {
            for (i, w) in els.enumerated() {
                let dumpBuf = NSMutableString()
                dump(w, depth: 0, out: dumpBuf, maxLevel: 7)
                buf.append("— \(attr)[\(i)] —\n\(dumpBuf)")
            }
        } else if let v0 = v, CFGetTypeID(v0) == AXUIElementGetTypeID() {
            let single = v0 as! AXUIElement
            let dumpBuf = NSMutableString()
            dump(single, depth: 0, out: dumpBuf, maxLevel: 7)
            buf.append("— \(attr) 單元素 —\n\(dumpBuf)")
        }
    }
    out += buf as String
    try? out.write(toFile: "/tmp/ax_scrape_report.log", atomically: true, encoding: .utf8)
    print("AX scrape 完成 → /tmp/ax_scrape_report.log")
}

private func probeFinish(_ report: NSMutableString, _ restore: TISInputSource?) {
    if let restore = restore {
        TISSelectInputSource(restore)
        probePump(0.5)
        report.append("\n（已還原先前輸入源）\n")
    }
    probeWriteLog(report as String)
    print(String(report))
    fflush(stdout)
    exit(0)
}

private func runProbeInjectOneShot() {
    let report = NSMutableString()
    print("PROBE: 進入取樣流程")
    fflush(stdout)
    _ = NSApplication.shared
    // regular 策略＋NSApp.run 事件循環，令視窗能真正取得鍵盤焦點
    NSApp.setActivationPolicy(.regular)

    // ① 記住目前輸入源；② TISSelect 失敗則用 Ctrl+Space 循環切換
    report.append("切換到點心輸入法…\n")
    var prevSource: TISInputSource? = nil
    let allList = TISCreateInputSourceList(nil, false)?.takeRetainedValue() as? [TISInputSource] ?? []
    for s in allList {
        let idPtr = TISGetInputSourceProperty(s, kTISPropertyInputSourceID)
        let idStr = idPtr != nil ? (Unmanaged<CFString>.fromOpaque(idPtr!).takeUnretainedValue() as String) : ""
        let selPtr = TISGetInputSourceProperty(s, kTISPropertyInputSourceIsSelected)
        let sel = selPtr != nil ? CFBooleanGetValue(Unmanaged<CFBoolean>.fromOpaque(selPtr!).takeUnretainedValue()) : false
        if sel {
            report.append("  目前輸入源 = \(idStr)\n")
            prevSource = s
            break
        }
    }
    var switched = probeSourceID(prevSource).contains("Squirrel") ? true : probeSelectSquirrel()
    if !switched {
        for attempt in 0...12 {
            let evD = CGEvent(keyboardEventSource: nil, virtualKey: 49, keyDown: true)
            evD?.flags = .maskControl
            evD?.post(tap: .cghidEventTap)
            probePump(0.05)
            let evU = CGEvent(keyboardEventSource: nil, virtualKey: 49, keyDown: false)
            evU?.flags = .maskControl
            evU?.post(tap: .cghidEventTap)
            probePump(0.55)
            if isSquirrelActive() {
                switched = true
                report.append("  Ctrl+Space 第 \(attempt) 次循環後轉到點心輸入法\n")
                break
            }
        }
    }
    report.append("  結果：\(switched ? "成功" : "失敗")\n")
    if !switched { probeFinish(report, prevSource); return }

    // ③【實驗】建造與 SquirrelPanel 同款的 shielding nonactivating NSPanel＋NSTextView，
    //    再讓子行程跨進程刮自己，驗證外部 AX 對這種面板的完整可達路徑
    let panel = NSPanel(contentRect: NSRect(x: 200, y: 300, width: 360, height: 120),
                        styleMask: [.nonactivatingPanel], backing: .buffered, defer: false)
    panel.level = NSWindow.Level(Int(CGShieldingWindowLevel()))
    panel.isOpaque = false
    panel.backgroundColor = .clear
    let holder = NSView(frame: NSRect(x: 0, y: 0, width: 360, height: 120))
    let text = NSTextView(frame: NSRect(x: 8, y: 8, width: 344, height: 104))
    text.string = "hkr\n1. 今日 2. 今夜 3. 尋"
    holder.addSubview(text)
    panel.contentView = holder

    let pid = Int(getpid())
    let scraper = Process()
    scraper.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    scraper.arguments = ["--ax-scrape-pid", String(pid)]
    scraper.standardOutput = FileHandle.nullDevice
    scraper.standardError = FileHandle.nullDevice

    func probeWorkStage() {
        panel.orderFront(nil)
        probePump(0.5)
        do {
            try scraper.run()
        } catch {
            print("PROBE: scraper 啟動失敗 \(error)")
            fflush(stdout)
        }
        probePump(4.0)
        // 收集 scraper 結果供報告
        if let data = try? Data(contentsOf: URL(fileURLWithPath: "/tmp/ax_scrape_report.log")),
           let s = String(data: data, encoding: .utf8) {
            report.append("\n【跨進程 AX 實驗結果】\n\(s)\n")
        } else {
            report.append("\n【跨進程 AX 實驗結果】無輸出（scraper 未完成）\n")
        }
        probeFinish(report, prevSource)
    }

    DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { probeWorkStage() }
    NSApp.run()
}

// 旗標檢查先於 daemon 主流程執行：--probe-inject 取樣診斷；--ax-scrape-pid AX 可達性實驗；
// --ax-listen 即時監聽；--self-test 報讀邏輯自檢
if CommandLine.arguments.contains("--probe-inject") {
    runProbeInjectOneShot()
} else if CommandLine.arguments.contains("--ax-scrape-pid") {
    probeAXScrapePID()
} else if let li = CommandLine.arguments.firstIndex(of: "--ax-listen"), CommandLine.arguments.count > li + 1 {
    axListen(seconds: Int(CommandLine.arguments[li + 1]) ?? 60)
}

// 【AX 即時監聽器】--ax-listen <秒>：以 10Hz 掃描全域視窗（CGWindowList，層級≥20）
// ＋Squirrel／IMK 系統宿主進程，並對每個視窗做「全屬性值」深度 dump，
// 變化才寫入 /tmp/dimsum_ax_listen.log——用於在真實組字期間捕捉候選窗的確切宿主與文字位置。
private func axListen(seconds: Int) {
    let logPath = "/tmp/dimsum_ax_listen.log"
    try? "=== ax-listen 開始 ===\n".write(toFile: logPath, atomically: true, encoding: .utf8)
    let fh = FileHandle(forWritingAtPath: logPath)

    func append(_ s: String) {
        guard let fh = fh else { return }
        fh.seekToEndOfFile()
        fh.write(s.data(using: .utf8) ?? Data())
    }

    // 全屬性值深度 dump（字串/數值屬性都帶出值；子樹深度≤4）
    func fullAttrDump(_ el: AXUIElement, depth: Int, out: inout String) -> Bool {
        guard depth <= 4, out.count < 6000 else { return false }
        var roleRef: CFTypeRef?
        AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &roleRef)
        let role = (roleRef as? String) ?? "?"
        // 跳過選單樹（噪音）；面板＝Window→Text 類節點
        if role == "AXMenuBar" || role == "AXMenuBarItem" || role == "AXMenu" || role == "AXMenuItem" || role == "AXDialog" {
            return false
        }
        out += String(repeating: " ", count: depth) + "role=\(role)\n"
        var namesRef: CFArray?
        AXUIElementCopyAttributeNames(el, &namesRef)
        let names = (namesRef as? [String]) ?? []
        for name in names {
            var v: CFTypeRef?
            AXUIElementCopyAttributeValue(el, name as CFString, &v)
            if let sv = v as? String, !sv.isEmpty {
                out += String(repeating: " ", count: depth) + "  \(name)=\"\(sv.prefix(300))\"\n"
            } else if let nv = v as? NSNumber {
                out += String(repeating: " ", count: depth) + "  \(name)=\(nv)\n"
            }
        }
        var kidsRef: CFTypeRef?
        AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &kidsRef)
        if let kids = kidsRef as? [AnyObject] {
            for k in kids {
                if fullAttrDump(k as! AXUIElement, depth: depth + 1, out: &out) { return true }
            }
        }
        return false
    }

    let interestingOwner: (String) -> Bool = { name in
        for key in ["Squirrel", "TextInput", "Cursor", "imk", "IMK", "Input Method", "Panel"] where name.contains(key) {
            return true
        }
        return false
    }

    var dumpedSigs: [pid_t: String] = [:]   // pid → 最近一次 dump 的內容簽章
    for tick in 0..<(seconds * 10) {
        var preScan = ""
        var report = ""
        let cg = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        var interestingPids: Set<pid_t> = []
        for w in cg {
            let layer = (w[kCGWindowLayer as String] as? NSNumber)?.intValue ?? -999
            let pid = (w[kCGWindowOwnerPID as String] as? NSNumber)?.intValue ?? 0
            let owner = (w[kCGWindowOwnerName as String] as? String) ?? "?"
            let onScreen = layer >= 20 || interestingOwner(owner)
            guard onScreen else { continue }
            interestingPids.insert(pid_t(pid))
            if layer >= 20 {
                report += "CG: pid=\(pid) owner=\(owner) layer=\(layer)\n"
            }
        }
        // IMK 系統宿主 + Squirrel 名下進程全都納入
        for app in NSWorkspace.shared.runningApplications {
            let bn = app.bundleIdentifier ?? ""
            let nm = app.localizedName ?? ""
            if bn.contains("im.rime") || interestingOwner(nm) {
                interestingPids.insert(app.processIdentifier)
            }
        }
        for pid in interestingPids {
            // 高速門檻式：每 tick 只做便宜的 windows 枚舉；
            // 「有窗且內容簽章變化」才深度 dump（避開選單樹噪音與慢掃）
            let axApp = AXUIElementCreateApplication(pid)
            var winsValue: CFTypeRef?
            let err = AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &winsValue)
            var wins = (winsValue as? [AnyObject]) ?? []
            if wins.isEmpty {
                var kidsValue: CFTypeRef?
                AXUIElementCopyAttributeValue(axApp, kAXChildrenAttribute as CFString, &kidsValue)
                let kids = (kidsValue as? [AnyObject]) ?? []
                if !kids.isEmpty { wins = kids }
            }
            if wins.isEmpty {
                if tick % 20 == 0 {
                    preScan += "AX pid=\(pid) 無窗（windows err=\(err)）\n"
                }
                continue
            }
            // 簽章：每次 tick 計算視窗數量＋第一個可見值
            var sig = "n=\(wins.count)"
            for w in wins {
                var valRef: CFTypeRef?
                AXUIElementCopyAttributeValue(w as! AXUIElement, kAXValueAttribute as CFString, &valRef)
                sig += "@\((valRef as? String)?.count ?? -1)"
            }
            if dumpedSigs[pid] == sig { continue }
            dumpedSigs[pid] = sig
            var tree = ""
            for w in wins {
                _ = fullAttrDump(w as! AXUIElement, depth: 0, out: &tree)
                tree += "----\n"
            }
            report += "AX pid=\(pid) windows=\(wins.count)\n\(tree)"
        }
        if !report.isEmpty {
            append("[tick \(tick)]\n\(preScan)\(report)\n")
        } else if !preScan.isEmpty {
            append("[tick \(tick)]\n\(preScan)\n")
        }
        usleep(100_000)
    }
    append("=== ax-listen 結束 ===\n")
    fh?.closeFile()
    print("ax-listen 完成 → \(logPath)")
    exit(0)
}

// ============ Paths ============
func getScriptDir() -> String {
    let arg0 = CommandLine.arguments[0]
    let dir = (arg0 as NSString).deletingLastPathComponent
    let absolute: String
    if dir.hasPrefix("/") {
        absolute = dir
    } else {
        absolute = FileManager.default.currentDirectoryPath + "/" + dir
    }
    return (absolute as NSString).standardizingPath
}

/// 本助手所在 .app 的上層資源目錄（DimsumManager.app/Contents/Resources）。
func getResourcesDir() -> String {
    let exeDir = getScriptDir()
    let bundleDir = (exeDir as NSString).deletingLastPathComponent      // …/DimsumVoiceHelper.app/Contents
    let appDir = (bundleDir as NSString).deletingLastPathComponent      // …/DimsumVoiceHelper.app
    if appDir.hasSuffix(".app") {
        let parent = (appDir as NSString).deletingLastPathComponent
        if (parent as NSString).lastPathComponent == "Helpers" {
            let contents = (parent as NSString).deletingLastPathComponent
            if (contents as NSString).lastPathComponent == "Contents" {
                return (contents as NSString).appendingPathComponent("Resources")
            }
        }
        return parent
    }
    return exeDir
}

func getConfigDir() -> String {
    // 設定與日誌寫入使用者目錄（與管理器「開啟日誌」一致）；
    // 不寫入 app bundle 內部，避免破壞程式簽名
    return NSHomeDirectory() + "/Library/Application Support/DimsumVoiceOver"
}
func getConfigPath() -> String {
    return getConfigDir() + "/dimsum_vo_helper.json"
}
func getLogPath() -> String {
    return getConfigDir() + "/dimsum_vo_helper.log"
}

// ============ Logging ============
var loggingEnabled = false
var logFile: FileHandle?

func log(_ message: String) {
    guard loggingEnabled else { return }
    let timestamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
    let line = "[\(timestamp)] \(message)\n"
    if let fh = logFile {
        if let data = line.data(using: .utf8) { fh.write(data) }
    }
}

func initLogging() {
    guard loggingEnabled else { return }
    let dir = getConfigDir()
    try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    FileManager.default.createFile(atPath: getLogPath(), contents: nil)
    logFile = FileHandle(forWritingAtPath: getLogPath())
    log("=== 日誌開始 ===")
}

/// 依 config.loggingEnabled 套用日誌開關（啟動載入設定後＋管理器 reload-config 時呼叫）。
/// 開啟且尚未有 file handle → 建檔開啟；關閉 → 停止寫入（檔案保留，由「清除／刪除」按鈕管理）。
func applyLoggingState() {
    let want = config.loggingEnabled
    if want {
        loggingEnabled = true
        if logFile == nil {
            let dir = getConfigDir()
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: getLogPath(), contents: nil)
            logFile = FileHandle(forWritingAtPath: getLogPath())
            log("=== 日誌開始（管理器設定：開啟） ===")
        }
    } else {
        loggingEnabled = false
        logFile?.closeFile()
        logFile = nil
    }
}

// ============ Configuration ============
// HelperConfig 已由 SharedSettingsView.swift 的 HelperConfigFile 提供
// （管理 app 與報讀助手共用的單一真相；欄位完全一致）
typealias HelperConfig = HelperConfigFile

var config = HelperConfig()
var inSettingsMode = false
var isComposing = false

func saveConfig() {
    let path = getConfigPath()
    let dir = getConfigDir()
    do {
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    } catch {
        print("無法建立目錄 \(dir): \(error)")
        return
    }
    do {
        let data = try JSONEncoder().encode(config)
        try data.write(to: URL(fileURLWithPath: path))
        log("配置已儲存到 \(path)")
    } catch {
        print("無法儲存配置到 \(path): \(error)")
    }
}

func loadConfigFromFile() {
    let path = getConfigPath()
    if let data = try? Data(contentsOf: URL(fileURLWithPath: path)) {
        if let saved = try? JSONDecoder().decode(HelperConfig.self, from: data) {
            config = saved
            // 遷移：舊版預設快捷鍵 Ctrl+Shift+Option+D 現改為預設關閉；
            // 用戶自訂的其他快捷鍵保持不變
            if config.settingsHotkey == "Ctrl+Shift+Option+D" { config.settingsHotkey = "" }
            log("配置已載入: ttsMode=\(config.ttsMode), rate=\(config.speechRate), desc=\(config.descriptionsEnabled), vol=\(config.volume), verbosity=\(config.verbosity), hotkey=\(config.settingsHotkey.isEmpty ? "（未啟用）" : config.settingsHotkey)")
        }
    }
}

// ============ 發音表（NVDA 字典轉換） ============
var pronunciations: [String: String] = [:]

func loadPronunciations(preferUserData: Bool = true) {
    let resourcesDir = getResourcesDir()
    var jsonPaths = [
        resourcesDir + "/voiceover_output/DimsumPronunciations.json",
        resourcesDir + "/DimsumPronunciations.json",
    ]
    if preferUserData {
        jsonPaths.insert(getConfigDir() + "/voiceover_output/DimsumPronunciations.json", at: 0)
    }
    for path in jsonPaths {
        if FileManager.default.fileExists(atPath: path) {
            if let data = try? Data(contentsOf: URL(fileURLWithPath: path)) {
                if let loaded = try? JSONDecoder().decode([String: String].self, from: data) {
                    pronunciations = loaded
                    log("已載入 \(pronunciations.count) 條發音規則 from JSON: \(path)")
                    return
                }
            }
        }
    }
    let swiftPaths = [
        resourcesDir + "/voiceover_output/DimsumPronunciations.swift",
        resourcesDir + "/DimsumPronunciations.swift",
    ]
    for path in swiftPaths {
        if FileManager.default.fileExists(atPath: path) {
            if let content = try? String(contentsOfFile: path, encoding: .utf8) {
                let pattern = #/"([^"]+)":\s*"((?:[^"\\]|\\.)*)"/#
                for match in content.matches(of: pattern) {
                    let key = String(match.1)
                    var value = String(match.2)
                    value = value.replacingOccurrences(of: "\\\"", with: "\"")
                    value = value.replacingOccurrences(of: "\\n", with: " ")
                    value = value.replacingOccurrences(of: "\\r", with: " ")
                    value = value.replacingOccurrences(of: "\\\\", with: "\\")
                    pronunciations[key] = value
                }
                log("已載入 \(pronunciations.count) 條發音規則 from Swift: \(path)")
                return
            }
        }
    }
    log("警告：未找到發音表")
}

/// 字表用半形空格分隔各組詞例，同一組內的逗號與別稱一併朗讀。
func processText(_ text: String, allExamples: Bool = false) -> String {
    guard config.descriptionsEnabled else { return text }
    return pronunciationText(text, using: pronunciations, allExamples: allExamples)
}

// ============ TIS Input Source Check ============
func isSquirrelActive() -> Bool {
    let sources = TISCreateInputSourceList(nil, false)?.takeRetainedValue() as? [TISInputSource] ?? []
    for source in sources {
        let idPtr = TISGetInputSourceProperty(source, kTISPropertyInputSourceID)
        let idStr = idPtr != nil ? (Unmanaged<CFString>.fromOpaque(idPtr!).takeUnretainedValue() as String) : ""
        if idStr.contains("Squirrel") {
            let selectedPtr = TISGetInputSourceProperty(source, kTISPropertyInputSourceIsSelected)
            if let selectedVal = selectedPtr {
                let isSelected = Unmanaged<CFBoolean>.fromOpaque(selectedVal).takeUnretainedValue()
                if CFBooleanGetValue(isSelected) { return true }
            }
        }
    }
    return false
}

// ============ 語音模式 0：Sin-ji 粵語（助手獨立語音，合併式） ============
let speechSynthesizer = AVSpeechSynthesizer()

final class SpeechDelegate: NSObject, AVSpeechSynthesizerDelegate {
    func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        speechFinished()
    }
    func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        speechFinished()
    }
}
let speechDelegate = SpeechDelegate()

func cantoneseVoice() -> AVSpeechSynthesisVoice? {
    if let v = AVSpeechSynthesisVoice(identifier: "com.apple.voice.compact.zh-HK.Sinji") { return v }
    if let v = AVSpeechSynthesisVoice(identifier: "com.apple.voice.enhanced.zh-HK.Sinji") { return v }
    let voices = AVSpeechSynthesisVoice.speechVoices()
    if let v = voices.first(where: { $0.language.lowercased().contains("zh") && $0.language.lowercased().contains("hk") }) { return v }
    return nil
}

// ============ 純朗讀通道（plain）：跳過字元描述 ============
// 「已到第一個候選／已到最後一個候選／錯誤／第 N 個候選，共 X 位」等
// 純通知文字不是候選內容，逐字加註字義會令「七個字讀成一大串」——
// 依規格這類提示只需按原文朗讀；三個朗讀內容模式共用同一管線，全程適用。
// 候選詞／上屏詞語本身維持字元描述（其存在目的就是區分同音字）。

func announceSinji(_ text: String, plain: Bool = false, logText: Bool = true) {
    // 合併式：不打斷目前語句（快速連續 stop/speak 會令引擎死鎖），
    // 本語句完成後由 speechFinished() 立即播出最新待讀內容
    let processed = plain ? text : processText(text)
    let utterance = AVSpeechUtterance(string: processed)
    if let voice = cantoneseVoice() { utterance.voice = voice }
    utterance.rate = config.speechRate
    utterance.volume = config.volume
    speechSynthesizer.speak(utterance)
    if logText { log("Sin-ji 報讀: \(processed.prefix(100))") }
}

// ============ 語音模式 1：系統語音（say，打斷式） ============
var currentSayProcess: Process?

func announceSystem(_ text: String, plain: Bool = false) {
    let processed = plain ? text : processText(text)
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/say")
    let wpm = Int(100 + config.speechRate * 200)
    process.arguments = ["-r", "\(wpm)", processed]
    process.terminationHandler = { _ in
        speechFinished()
    }
    do {
        try process.run()
        currentSayProcess = process
        log("系統語音報讀: \(processed.prefix(100))")
    } catch {
        log("say 命令失敗: \(error)")
        engineBusy = false
    }
}

// ============ 語音模式 2：VoiceOver 整合 ============
// 透過 AXAnnouncement 通知朗讀內容，由 VoiceOver 以其自身語音與設定朗讀；
// 助手不再發聲，避免雙重語音。
func isVoiceOverRunning() -> Bool {
    NSWorkspace.shared.runningApplications.contains { $0.localizedName == "VoiceOver" }
}

func announceViaVoiceOver(_ text: String, plain: Bool = false) -> Bool {
    guard isVoiceOverRunning() else { return false }
    let processed = plain ? text : processText(text)
    // AXAnnouncement／AXPriority 為 AppKit 公告通知的官方常量字串，
    // 由 VoiceOver 攔聽並以其自身語音朗讀
    NSAccessibility.post(
        element: NSApp as Any,
        notification: NSAccessibility.Notification(rawValue: "AXAnnouncement"),
        userInfo: [
            NSAccessibility.NotificationUserInfoKey(rawValue: "AXAnnouncement"): processed,
            NSAccessibility.NotificationUserInfoKey(rawValue: "AXPriority"): 90
        ]
    )
    log("VoiceOver 通知朗讀: \(processed.prefix(100))")
    return true
}

// ============ 統一朗讀入口（合併佇列：最新內容優先、引擎不死鎖） ============
var pendingSpeech: String? = nil     // 引擎忙碌時暫存的最新內容（舊的丟棄）
var pendingSpeechPlain = false       // 最新待讀內容是否為「純朗讀」（跳過字元描述）
var engineBusy = false               // Sin-ji/say 目前是否在播

func announce(_ text: String, plain: Bool = false) {
    guard !text.isEmpty else { return }
    pendingSpeech = text             // 合併：只保留最新
    pendingSpeechPlain = plain
    pumpSpeech()
}

/// 已在呼叫端完成字元描述處理的文字：直接以純朗讀播出（播出層不再二次加工）
func announcePreprocessed(_ text: String) {
    announce(text, plain: true)
}

/// 引擎空閒且有待讀內容時播出
func pumpSpeech() {
    guard !engineBusy, let text = pendingSpeech else { return }
    let plain = pendingSpeechPlain
    pendingSpeech = nil
    engineBusy = true
    switch config.ttsMode {
    case 2:
        if !announceViaVoiceOver(text, plain: plain) {
            log("VoiceOver 未執行，暫用系統語音")
            announceSystem(text, plain: plain)
            return   // say 的 terminationHandler 會解除 engineBusy
        }
        engineBusy = false
    case 1:
        announceSystem(text, plain: plain)
        return   // say 的 terminationHandler 會解除 engineBusy
    default:
        announceSinji(text, plain: plain)
    }
}

var lastInterruptAt = Date.distantPast

/// 導航等需要即時回應的朗讀：打斷目前語音（距上次打斷 ≥0.25 秒才允許，避免快速打斷死鎖）
func announceInterrupt(_ text: String, plain: Bool = false) {
    let gap = max(0.1, config.navInterruptInterval)
    if Date().timeIntervalSince(lastInterruptAt) < gap {
        announce(text, plain: plain)   // 太頻繁：合併處理
        return
    }
    lastInterruptAt = Date()
    pendingSpeech = nil
    stopAnnouncement()
    announce(text, plain: plain)
}

func speechFinished() {
    DispatchQueue.main.async {
        engineBusy = false
        pumpSpeech()   // 有排隊的最新內容立即接續播出
    }
}

/// 即時插播：打斷目前語句，立即朗讀指定內容（僅用於方向鍵選字等離散操作；
/// 連續高頻打斷會令語音引擎死鎖，組字／上屏朗讀仍走合併佇列）
func announceNow(_ text: String, plain: Bool = false) {
    guard !text.isEmpty else { return }
    pendingSpeech = nil
    speechSynthesizer.stopSpeaking(at: .immediate)
    currentSayProcess?.terminate()
    currentSayProcess = nil
    engineBusy = false
    pendingSpeech = text
    pendingSpeechPlain = plain
    pumpSpeech()
}

func stopAnnouncement() {
    pendingSpeech = nil
    speechSynthesizer.stopSpeaking(at: .immediate)
    currentSayProcess?.terminate()
    currentSayProcess = nil
    engineBusy = false
}

/// 使用者主動停止（Ctrl／選單／停止按鈕）
func stopAllSpeech() {
    stopAnnouncement()
}

// ============ 取得聚焦文字框內容 ============
struct FocusedText {
    let element: AXUIElement
    let value: String
    let selection: NSRange?
    let markedRange: NSRange?
}

private func textRange(_ element: AXUIElement, _ attribute: String) -> NSRange? {
    var result: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, attribute as CFString, &result) == .success,
          let result, CFGetTypeID(result) == AXValueGetTypeID() else { return nil }
    var range = CFRange()
    guard AXValueGetValue(result as! AXValue, .cfRange, &range),
          range.location >= 0, range.length >= 0 else { return nil }
    return NSRange(location: range.location, length: range.length)
}

func getFocusedText() -> FocusedText? {
    var focused: CFTypeRef?
    let system = AXUIElementCreateSystemWide()
    AXUIElementSetMessagingTimeout(system, 0.1)
    guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
          let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
    let element = focused as! AXUIElement
    var value: CFTypeRef?
    AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value)
    guard let text = value as? String else { return nil }
    return FocusedText(element: element, value: text,
                       selection: textRange(element, kAXSelectedTextRangeAttribute),
                       markedRange: textRange(element, "AXMarkedTextRange"))
}

func getFocusedTextContent() -> String? {
    getFocusedText()?.value
}

// ============ 取得 Squirrel 候選窗內容（深層遞迴版） ============
// 【v2.5.17 根因修復】實機規格紀錄證實候選窗抓取 30/30 恆空（三模式因此塌成同一句）。
// AX 可達性實驗證明 shielding-level nonactivating NSPanel 跨進程完全可達
// （app.AXWindows → AXWindow → AXTextArea → AXValue＝「preedit\n1.候選…」），
// 本函式因此改為：①深度遞迴收集所有 AXValue（不只單層 children）
// ②AXWindows 列不出窗時改用 app.AXChildren 找 AXWindow
// ③面板出現有時差一兩幀：最多 4 次重試輪詢
// ④仍全空時寫一次性診斷 dump（/tmp/dimsum_panel_diag.log）供實機確認樹的形態
private var panelDiagLogged = false

private func axWindowsOf(_ axApp: AXUIElement) -> [AXUIElement] {
    var winsRef: CFTypeRef?
    AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &winsRef)
    if let wins = winsRef as? [AnyObject] {
        let els = wins.map { $0 as! AXUIElement }
        if !els.isEmpty { return els }
    }
    // 後備：有些面板不出現在 AXWindows，反而出現在 app.AXChildren
    AXUIElementCopyAttributeValue(axApp, kAXChildrenAttribute as CFString, &winsRef)
    if let kids = winsRef as? [AnyObject] {
        return kids.map { $0 as! AXUIElement }
    }
    return []
}

private func axCollectValues(_ el: AXUIElement, depth: Int, budget: inout Int, out: inout [String]) {
    guard depth <= 6, budget > 0 else { return }
    budget -= 1
    var valRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXValueAttribute as CFString, &valRef)
    if let s = valRef as? String, !s.isEmpty { out.append(s) }
    var kidsRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &kidsRef)
    if let kids = kidsRef as? [AnyObject] {
        for k in kids { axCollectValues(k as! AXUIElement, depth: depth + 1, budget: &budget, out: &out) }
    }
}

private var axManualAccessibilityDone: [pid_t: Bool] = [:]

func getSquirrelCandidateText(retries: Int = 3) -> String? {
    let runningApps = NSWorkspace.shared.runningApplications
    for attempt in 0..<(1 + retries) {
        for app in runningApps {
            // 以 bundle ID 辨認 Squirrel，不依賴本地化進程名稱。
            let isSquirrelApp = (app.bundleIdentifier == "im.rime.inputmethod.Squirrel")
                || (app.bundleIdentifier ?? "").hasPrefix("im.rime.")
                || (app.localizedName ?? "").contains("Squirrel")
            guard isSquirrelApp else { continue }
            let axApp = AXUIElementCreateApplication(app.processIdentifier)
            // 【AXManualAccessibility】部分 app（尤其 IMK 背景宿主）不自報無障礙樹，
            // 需由用戶端設定此屬性後窗口與文字才會完整暴露（Electron/Java 面板的標準手法）
            if axManualAccessibilityDone[app.processIdentifier] != true {
                axManualAccessibilityDone[app.processIdentifier] = true
                AXUIElementSetAttributeValue(axApp, "AXManualAccessibility" as CFString, kCFBooleanTrue)
                log("已嘗試設定 AXManualAccessibility pid=\(app.processIdentifier)")
            }
            for win in axWindowsOf(axApp) {
                var texts: [String] = []
                var budget = 60
                axCollectValues(win, depth: 0, budget: &budget, out: &texts)
                // 【第二文字源】未聽見 AXValue 時，同時檢查選中文字（高亮候選）與描述類屬性
                for attr in ["AXSelectedText", "AXDescription", "AXTitle"] {
                    var selRef: CFTypeRef?
                    AXUIElementCopyAttributeValue(win, attr as CFString, &selRef)
                    if let s = selRef as? String, !s.isEmpty { texts.append(s) }
                }
                let joined = texts.joined(separator: " ")
                if !joined.trimmingCharacters(in: .whitespaces).isEmpty {
                    log("候選窗抓取成功：嘗試=\(attempt) 值=\"\(joined.prefix(120))\"")
                    return joined
                }
            }
        }
        if attempt < retries { usleep(80000) }
    }
    // 【診斷 dump】初次全空的一次性記錄：Squirrel／IMK 宿主全部進程、全部屬性帶值
    if !panelDiagLogged {
        panelDiagLogged = true
        var dumpOut = ""
        for app in NSWorkspace.shared.runningApplications {
            let bn = app.bundleIdentifier ?? ""
            let nm = app.localizedName ?? ""
            let interesting = bn.contains("im.rime") || bn.contains("TextInput")
                || bn.contains("Cursor") || bn.contains("InputMethod")
                || nm.contains("Squirrel") || nm.contains("TextInput") || nm.contains("Cursor")
            guard interesting else { continue }
            let axApp = AXUIElementCreateApplication(app.processIdentifier)
            dumpOut += "◆ pid=\(app.processIdentifier) 名=\(nm) bundle=\(bn)\n"
            for win in axWindowsOf(axApp) {
                _ = axDumpTreeFull(win, depth: 0, out: &dumpOut)
                dumpOut += "----\n"
            }
            var appDump = ""
            _ = axDumpTreeFull(axApp, depth: 0, out: &appDump)
            if !appDump.isEmpty { dumpOut += "（app 層）\n\(appDump)" }
        }
        try?("時間：\(Date())\n\(dumpOut)").write(toFile: "/tmp/dimsum_panel_diag.log", atomically: true, encoding: .utf8)
        log("候選窗抓取仍空：已寫深度診斷（全屬性值）到 /tmp/dimsum_panel_diag.log（\(dumpOut.count) 字元）")
    }
    return nil
}

// 【深度診斷】除了屬性名，還把字串/數值屬性的值一併取出（找出文字真正所在屬性）
private func axDumpTreeFull(_ el: AXUIElement, depth: Int, out: inout String) -> Bool {
    guard depth <= 5, out.count < 6000 else { return false }
    var roleRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &roleRef)
    let role = (roleRef as? String) ?? "?"
    out += String(repeating: " ", count: depth) + "role=\(role)\n"
    var namesRef: CFArray?
    AXUIElementCopyAttributeNames(el, &namesRef)
    let names = (namesRef as? [String]) ?? []
    for name in names {
        var v: CFTypeRef?
        AXUIElementCopyAttributeValue(el, name as CFString, &v)
        if let sv = v as? String, !sv.isEmpty {
            out += String(repeating: " ", count: depth) + "  \(name)=\"\(sv.prefix(300))\"\n"
        } else if let nv = v as? NSNumber {
            out += String(repeating: " ", count: depth) + "  \(name)=\(nv)\n"
        }
    }
    var kidsRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &kidsRef)
    if let kids = kidsRef as? [AnyObject] {
        for k in kids {
            if axDumpTreeFull(k as! AXUIElement, depth: depth + 1, out: &out) { return true }
        }
    }
    return false
}

// ============ 差異朗讀核心（修復重複朗讀整行的 Bug） ============
/// 比較新舊文字，回傳「新增的部分」；若為刪除回傳 nil 並回報刪除字元
struct FieldDiff {
    var added: String        // 新增的文字
    var removed: String      // 被刪除的文字（刪鍵時使用）
    var isUnchanged: Bool
}

func diffField(old: String, new: String) -> FieldDiff {
    if old == new { return FieldDiff(added: "", removed: "", isUnchanged: true) }
    let o = Array(old), n = Array(new)
    var prefix = 0
    while prefix < o.count && prefix < n.count && o[prefix] == n[prefix] { prefix += 1 }
    var suffix = 0
    while suffix < o.count - prefix && suffix < n.count - prefix && o[o.count - 1 - suffix] == n[n.count - 1 - suffix] { suffix += 1 }
    let added = String(n[prefix..<(n.count - suffix)])
    let removed = String(o[prefix..<(o.count - suffix)])
    return FieldDiff(added: added, removed: removed, isUnchanged: false)
}

/// 候選清單在組字區域中的起點索引（nil＝清單不在區域內）。
/// 數學確定性：schema 的 `style/inline_candidate: true` + `candidate_format: "%c. %@"`
/// 令候選清單必定以「1.」開始；同時 speller `alphabet` 不含 0-9（數字經 algebra 轉字母），
/// 故輸入碼中不可能出現數字——組字區域內的「1.」只可能是候選清單起點。
func candidateListStart(_ region: String) -> String.Index? {
    region.range(of: "1.")?.lowerBound
}

/// 從候選窗文字統計目前頁的候選位數（0＝讀不到清單）
func candidateCount(from windowText: String) -> Int {
    guard !windowText.isEmpty else { return 0 }
    let nums = windowText.matches(of: #/(\d+)\./#).compactMap { m in Int(m.1) }.filter { $0 >= 1 && $0 <= 9 }
    return Set(nums).count
}

// ============ 【字典鏡像引擎】（規格：優先引擎結構化資料） ============
// 實測根因：macOS 26 上可見候選面板＝Squirrel 進程 shield 層視窗（AXWindows 恆空、
// app 側 AXTextArea NumberOfCharacters=0）——AX 面板文字線路全部不通。
// 改以與引擎同源的 dimsum_cb 字表建「碼 → 候選（權重降序）」前綴索引：
//   v1＝帶編號候選清單、v2＝候選總數、數字鍵選字定位、邊界上限皆由此供值；
// preedit／組字區域仍供 v0 進度與上屏後備。
var dictWordArr: [String] = []
var dictNormArr: [String] = []
var dictWeightArr: [Int] = []
var dictSortedIdx: [Int] = []      // （舊二分索引，保留相容；查表已改用 dictNormBucket）
var dictNormBucket: [String: [Int]] = [:]   // normalized 碼 → 行號（桶式完全相符查表）
var dictReady = false

/// 套用 schema speller algebra：0-9→A-N、g→G、h→H（與引擎輸入碼同一正規化空間）
func dictNormalizeSpelling(_ s: String) -> String {
    // 【問題一根因修復（round_024）】點字碼是**大小寫敏感**的（schema 字母表區分大小寫）：
    // 舊版 s.uppercased() 把小寫碼 b@（呀）與數字碼 2@→B@（怨）合併進同一候選桶，
    // 靜態權重讓怨搶走首位 → 朗讀「怨」而面板是「呀」（字對音錯的最終根因）。
    // 正確語義＝僅套 speller algebra：數字→字母（數字無大小寫問題）＋小寫 g/h→大寫；
    // 其餘字元**保留原大小寫**。
    var out = ""
    for ch in s {
        switch ch {
        case "1": out.append("A")
        case "2": out.append("B")
        case "3": out.append("C")
        case "4": out.append("D")
        case "5": out.append("E")
        case "6": out.append("F")
        case "7": out.append("I")
        case "8": out.append("J")
        case "9": out.append("K")
        case "0": out.append("N")
        case "g": out.append("G")
        case "h": out.append("H")
        default: out.append(ch)
        }
    }
    return out
}

func loadDimsumDictNow(preferUserData: Bool = true) -> Bool {
    // 平日優先使用已部署的字典，自檢驗證隨程式附上的資料。
    var paths = [getResourcesDir() + "/rime/dimsum_cb.dict.yaml"]
    if preferUserData {
        paths.insert(NSHomeDirectory() + "/Library/Rime/dimsum_cb.dict.yaml", at: 0)
    }
    var content: String? = nil
    var usedPath = ""
    for p in paths {
        if let c = try? String(contentsOfFile: p, encoding: .utf8) { content = c; usedPath = p; break }
    }
    guard let text = content else {
        log("字典鏡像：找不到 dimsum_cb.dict.yaml（候選清單將無法鏡像）")
        return false
    }
    var words: [String] = []
    var norms: [String] = []
    var weights: [Int] = []
    var loadedPath = usedPath
    for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
        let l = String(line)
        if l.isEmpty || l.hasPrefix("#") || l == "---" || l == "..." { continue }
        let comps = l.components(separatedBy: "\t")
        guard comps.count >= 2 else { continue }
        let word = comps[0].trimmingCharacters(in: .whitespaces)
        let code = comps[1].trimmingCharacters(in: .whitespaces)
        guard !word.isEmpty, !code.isEmpty else { continue }
        let weight = comps.count > 2 ? (Int(comps[2].trimmingCharacters(in: .whitespaces)) ?? 0) : 0
        words.append(word)
        norms.append(dictNormalizeSpelling(code))
        weights.append(weight)
    }
    // 併入用家詞庫 dimsum_cb.user.yaml（import_tables 有引用；讓自訂詞與頻率也進鏡像）
    let userDictPath = NSHomeDirectory() + "/Library/Rime/dimsum_cb.user.yaml"
    if preferUserData, let userText = try? String(contentsOfFile: userDictPath, encoding: .utf8) {
        var userCount = 0
        for line in userText.split(separator: "\n", omittingEmptySubsequences: true) {
            let l = String(line)
            if l.isEmpty || l.hasPrefix("#") || l == "---" || l == "..." { continue }
            let comps = l.components(separatedBy: "\t")
            guard comps.count >= 2 else { continue }
            let word = comps[0].trimmingCharacters(in: .whitespaces)
            let code = comps[1].trimmingCharacters(in: .whitespaces)
            guard !word.isEmpty, !code.isEmpty else { continue }
            let weight = comps.count > 2 ? (Int(comps[2].trimmingCharacters(in: .whitespaces)) ?? 0) : 0
            words.append(word)
            norms.append(dictNormalizeSpelling(code))
            weights.append(weight)
            userCount += 1
        }
        if userCount > 0 { loadedPath += " + 用家詞庫(\(userCount) 條)" }
    }
    // 【對表引擎（2026-11-17 重修）】桶式完全相符查表：
    // 舊版把「CJK 詞優先」做進全域排序再用二分搜尋——排序鍵摻入 isCJK 後陣列
    // **不再是全域有序**，二分前提被破壞，標點條目（——、。，引號等非 CJK 詞）
    // 全數落入陣列尾段而永遠查不到（實錄：碼="--" 表候選=0，字典明明有「——↹--↹999206」）。
    // 改為 normalized 碼→行號的 Dictionary 桶，O(1) 完全相符，桶內排序：CJK 詞優先、
    // 同 CJK 屬性內權重降序（平手按行號，穩定可預期）。
    var bucket: [String: [Int]] = [:]
    for (i, n) in norms.enumerated() {
        bucket[n, default: []].append(i)
    }
    for (k, v) in bucket {
        bucket[k] = v.sorted { a, b in
            let aCJK = words[a].containsCJK()
            let bCJK = words[b].containsCJK()
            if aCJK != bCJK { return aCJK }   // CJK 詞在前（同步 Rime punct_translator 分離語義）
            if weights[a] != weights[b] { return weights[a] > weights[b] }   // 權重降序
            return a < b                      // 穩定：按行號
        }
    }
    let summary = "字典鏡像載入完成：\(words.count) 條（\(Set(norms).count) 個碼）來源=\(loadedPath)"
    let publish = {
        dictNormBucket = bucket
        dictWordArr = words
        dictNormArr = norms
        dictWeightArr = weights
        dictReady = true
        log(summary)
    }
    if Thread.isMainThread { publish() } else { DispatchQueue.main.sync(execute: publish) }
    return true
}

func loadDimsumDictAsync() {
    DispatchQueue.global(qos: .utility).async {
        _ = loadDimsumDictNow()
    }
}

struct RimeCandidatePage {
    let schema: String
    let input: String
    let segmentStart: Int
    let highlighted: Int
    let hasOnlyOneCandidate: Bool
    let candidates: [String]

    var currentCode: String { String(decoding: input.utf8.dropFirst(segmentStart), as: UTF8.self) }

    init?(_ report: String) {
        let fields = report.components(separatedBy: "\0")
        let firstCandidate = fields.first == "dimsum-2" ? 8 : 6
        guard (firstCandidate...(firstCandidate + 9)).contains(fields.count),
              fields[0] == "dimsum-1" || fields[0] == "dimsum-2",
              ["dimsum_cb", "dimsum_cb_std", "dimsum_full"].contains(fields[1]),
              let start = Int(fields[3]), (0...fields[2].utf8.count).contains(start),
              let position = Int(fields[4]), (0...(fields.count - firstCandidate)).contains(position),
              fields[5] == "0" || (fields[5] == "1" && fields.count == firstCandidate + 1) else { return nil }
        schema = fields[1]
        input = fields[2]
        segmentStart = start
        highlighted = position
        hasOnlyOneCandidate = fields[5] == "1"
        candidates = Array(fields.dropFirst(firstCandidate))
    }

    func isSameSegment(as other: RimeCandidatePage) -> Bool {
        schema == other.schema && input == other.input && segmentStart == other.segmentStart
    }

    func hasSameCandidateList(as other: RimeCandidatePage?) -> Bool {
        guard let other else { return false }
        return isSameSegment(as: other) && hasOnlyOneCandidate == other.hasOnlyOneCandidate
            && candidates == other.candidates
    }

    func followsSegment(of previous: RimeCandidatePage?) -> Bool {
        guard let previous, !input.isEmpty else { return false }
        return schema == previous.schema && input == previous.input && segmentStart > previous.segmentStart
    }
}

struct RimeCandidateReport {
    let page: RimeCandidatePage
    let revision: String
    let isSelection: Bool
    let writtenAt: Date

    init?(_ payload: String, writtenAt: Date, legacyRevision: String) {
        guard let page = RimeCandidatePage(payload) else { return nil }
        let fields = payload.components(separatedBy: "\0")
        self.page = page
        self.writtenAt = writtenAt
        if fields[0] == "dimsum-2" {
            guard fields[6] == "selection" || fields[6] == "update", !fields[7].isEmpty else { return nil }
            isSelection = fields[6] == "selection"
            revision = fields[7]
        } else {
            isSelection = false
            revision = legacyRevision
        }
    }
}

func rimeReportDirectory() -> String {
    NSHomeDirectory() + "/Library/Rime/dimsum-reporting"
}

func readRimeCandidateReport() -> RimeCandidateReport? {
    let path = rimeReportDirectory() + "/candidates"
    guard let attributes = try? FileManager.default.attributesOfItem(atPath: path),
          let writtenAt = attributes[.modificationDate] as? Date,
          let length = attributes[.size] as? NSNumber, length.intValue <= 32768,
          let payload = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
    let inode = (attributes[.systemFileNumber] as? NSNumber)?.stringValue ?? ""
    return RimeCandidateReport(payload, writtenAt: writtenAt,
                               legacyRevision: "\(inode):\(writtenAt.timeIntervalSince1970)")
}

func readRimeCandidatePage(since earliest: Date) -> RimeCandidatePage? {
    guard let report = readRimeCandidateReport(), report.writtenAt >= earliest else { return nil }
    return report.page
}

/// 快速報讀每個字的第一組配詞。數字放在對應選項前面。
func candidateAnnouncement(_ candidates: [String], numbered: Bool) -> String {
    candidates.enumerated().map { index, word in
        var description = processText(word)
        if description.count > 1, description.hasSuffix("。") { description.removeLast() }
        return numbered ? "\(index + 1)，\(description)" : description
    }.joined(separator: "。")
}

/// Rime 的 inline preview 包含實際高亮候選，有時尾部會帶未轉換的碼。
/// 主詞庫的靜態權重不反映記憶版的用家排序。
func resolveProgressBase(preeditBase: String, candidates: [String], compositionCode: String) -> String {
    if preeditBase.containsCJK() {
        if let last = preeditBase.lastIndex(where: { String($0).containsCJK() }) {
            let tail = cleanPreeditText(String(preeditBase[preeditBase.index(after: last)...]))
            if !tail.isEmpty, compositionCode.hasSuffix(tail) {
                return String(preeditBase[...last]).trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        return preeditBase
    }
    return candidates.first ?? (preeditBase.isEmpty ? compositionCode : preeditBase)
}

/// 【--self-test】驗證候選報告解析、朗讀內容、設定相容和字典查表。
private func runSelfTest() {
    var report = "=== dimsum self-test ===\n"
    var pass = true
    guard loadDimsumDictNow(preferUserData: false) else {
        print("FAIL: 字典載入失敗")
        exit(1)
    }
    loadPronunciations(preferUserData: false)
    report += "字典：\(dictWordArr.count) 條（\(Set(dictNormArr).count) 個碼）；發音表 \(pronunciations.count) 條\n"
    // 【對表引擎防回歸斷言（2026-11-17）】
    // ①標點條目必須可查（舊版 CJK-first 排序破壞二分搜尋前提，「——」的碼 -- 永遠 0 位）
    let dashCands = dictCandidates(for: "--")
    let dashOK = dashCands.contains("——")
    if !dashOK { pass = false }
    // ②並擊反查表必須含 fs→k（實錄「請呃」k 並擊丟失）、fjls→x、ls→-
    let chordOK = chordSetMap["fs"] == "k" && chordSetMap["fjls"] == "x" && chordSetMap["ls"] == "-"
    if !chordOK { pass = false }
    // ②b 反查表 vs schema chord_composer **全表比對**：解析 Resources 的 dimsum_cb.schema.yaml
    // 全部 xform 規則，每條的「ASCII 排序鍵組」必須存在於 chordSetMap 且碼字一致
    // （2026-11-17 手工表曾漏 fsk→o／錯排 adfsjk→Q 等 9 條——實機 braillitype 逮出，此斷言防永久回歸）
    var chordFullOK = true
    var chordFullDetail = ""
    if let stext = try? String(contentsOfFile: getResourcesDir() + "/rime/dimsum_cb.schema.yaml", encoding: .utf8) {
        for m in stext.matches(of: #/xform[/~]\^(.+?)\$[/~](.+?)[/~]/#) {
            var ch = String(m.2)
            if ch == "\\\\" { ch = "\\" }   // YAML/regex 雙跳脫的反斜線規則
            if ch.isEmpty { ch = "/" }      // 正斜線規則（char 本身是分隔符）
            let pat = String(String(m.1).sorted())
            if chordSetMap[pat] != ch {
                chordFullOK = false
                chordFullDetail += "\(pat)→\(ch) "
            }
        }
    } else {
        chordFullOK = false
        chordFullDetail = "schema 檔讀取失敗 "
    }
    if !chordFullOK { pass = false }
    config.descriptionsEnabled = true
    let shortText = processText("不過")
    let fullText = processText("不過", allExamples: true)
    let descriptionsOK = shortText == "不凡。過去，戈先生，戈小姐。"
        && fullText.contains("不久") && fullText.contains("過火")
    report += "標點碼及並擊反查：\(dashOK && chordOK && chordFullOK ? "PASS" : "FAIL（缺：\(chordFullDetail)）")\n"
    let namedCandidates = candidateAnnouncement(["日本", "不過"], numbered: true)
    let expectedJapan = processText("日").dropLast() + "。" + processText("本").dropLast()
    let firstGroupsOK = namedCandidates == "1，\(expectedJapan)。2，不凡。過去，戈先生，戈小姐"
    config.descriptionsEnabled = false
    let namesOnly = candidateAnnouncement(["自由", "文字", "治安"], numbered: false)
    let numbersFirst = candidateAnnouncement(["自由", "文字", "治安"], numbered: true)
    config.descriptionsEnabled = true
    let reportFields = ["dimsum-1", "dimsum_cb", "pKqo@nH,", "5", "1", "0", "你", "您"]
    let rimePage = RimeCandidatePage(reportFields.joined(separator: "\0"))
    let single = RimeCandidatePage(["dimsum-1", "dimsum_cb", "abc", "0", "1", "1", "自由"].joined(separator: "\0"))
    let empty = RimeCandidatePage(["dimsum-1", "dimsum_cb", "", "0", "0", "0"].joined(separator: "\0"))
    let stagedInput = "pKqo@nH,"
    let firstStage = RimeCandidatePage(["dimsum-2", "dimsum_cb", stagedInput, "0", "1", "0",
                                        "update", "session:1", "不過你", "不過"].joined(separator: "\0"))
    let middleStage = RimeCandidatePage(["dimsum-2", "dimsum_cb", stagedInput, "2", "1", "0",
                                         "selection", "session:2", "過你", "過"].joined(separator: "\0"))
    let nextSnapshot = RimeCandidateReport(["dimsum-2", "dimsum_cb", stagedInput, "5", "1", "1",
                                            "selection", "session:3", "你"].joined(separator: "\0"),
                                           writtenAt: .distantPast, legacyRevision: "")
    let sameStageUpdate = RimeCandidateReport(["dimsum-2", "dimsum_cb", stagedInput, "5", "1", "1",
                                               "update", "session:4", "你"].joined(separator: "\0"),
                                              writtenAt: .distantPast, legacyRevision: "")
    let legacy = try? JSONDecoder().decode(HelperConfig.self, from: Data(#"{"verbosity":2,"verboseProgressRead":true}"#.utf8))
    let emptyConfig = try? JSONDecoder().decode(HelperConfig.self, from: Data("{}".utf8))
    let existingSimple = try? JSONDecoder().decode(HelperConfig.self, from: Data(#"{"verbosity":0,"lookupHotkey":""}"#.utf8))
    let existingLineKey = try? JSONDecoder().decode(HelperConfig.self, from: Data(#"{"readLineHotkey":"Cmd+Shift+L"}"#.utf8))
    let existingLookupKey = try? JSONDecoder().decode(HelperConfig.self, from: Data(#"{"lookupHotkey":"Ctrl+Option+Cmd+."}"#.utf8))
    let lookupEntries = ["過": "過去，戈先生 過火 過錯", "日": "日子 日期", "本": "本來 本事"]
    let defaultLookup = HelperShortcut(HelperConfig.defaultLookupHotkey)
    let learned = resolveProgressBase(preeditBase: "亞", candidates: ["阿", "亞"], compositionCode: "b@")
    let mixed = resolveProgressBase(preeditBase: "不過nH,", candidates: ["不"], compositionCode: "pKqo@nH,")
    let partial = CandidateSelection(field: nil, priorPreview: "不過你", candidate: "",
                                     code: "pKqo@nH,", isDigit: true)
    let remainder = remainingComposition(after: partial, in: "不過你", marked: false)
    let fullSelection = CandidateSelection(field: nil, priorPreview: "不過你", candidate: "不過你",
                                           code: "pKqo@nH,", isDigit: true)
    let inserted = insertedText(before: "甲乙", selection: NSRange(location: 1, length: 0), after: "甲不過你乙")
    let schemaOptions = [RimeSchemaOption(id: "memory", name: "記憶版", lastUsed: 5),
                         RimeSchemaOption(id: "single", name: "一陽指", lastUsed: 10),
                         RimeSchemaOption(id: "standard", name: "標準版", lastUsed: 20)]
    let schemaOrder = rimeSchemaMenuOrder(schemaOptions, currentID: "memory", fixedOrder: false).map(\.id)
    let fixedOrder = rimeSchemaMenuOrder(schemaOptions, currentID: "standard", fixedOrder: true).map(\.id)
    let regressions = [
        ("每字第一組與導航全部配詞", descriptionsOK && firstGroupsOK),
        ("舊詳細模式轉為標準", legacy?.verbosity == 1),
        ("預設標準，保留既有精簡", HelperConfig().verbosity == 1 && emptyConfig?.verbosity == 1
            && existingSimple?.verbosity == 0),
        ("整行朗讀預設留空，保留已儲存按鍵", HelperConfig().readLineHotkey.isEmpty
            && emptyConfig?.readLineHotkey == "" && existingLineKey?.readLineHotkey == "Cmd+Shift+L"
            && HelperShortcut("") == nil),
        ("查配詞快捷鍵", emptyConfig?.lookupHotkey == HelperConfig.defaultLookupHotkey
            && existingSimple?.lookupHotkey == ""
            && existingLookupKey?.lookupHotkey == "Ctrl+Option+Cmd+."
            && defaultLookup?.matches(39, [.maskControl, .maskAlternate, .maskCommand]) == true
            && defaultLookup?.matches(39, [.maskControl, .maskAlternate]) == false
            && defaultLookup?.matches(47, [.maskControl, .maskAlternate, .maskCommand]) == false
            && HelperShortcut("Ctrl+Option+'")?.matches(39, [.maskControl, .maskAlternate]) == true
            && HelperShortcut("Ctrl+?") == nil),
        ("讀屏指令辨識", ScreenReaderLookup(bundleID: "com.dimsum.braille.lookup-voiceover") == .voiceOver
            && ScreenReaderLookup(bundleID: "com.dimsum.braille.lookup-turnover") == .turnOver
            && ScreenReaderLookup(bundleID: "unknown") == nil),
        ("最近報讀配詞", lookupPronunciation(" 過\n", using: lookupEntries) == "過去，戈先生 過火 過錯。"
            && lookupPronunciation("日本 過", using: lookupEntries) == "日子。本來。 過去，戈先生。"
            && lookupPronunciation("abc", using: lookupEntries) == "沒有找到配詞解釋。"),
        ("候選用配詞報讀預設開啟，保留用家設定", HelperConfig().descriptionsEnabled
            && legacy?.descriptionsEnabled == true
            && (try? JSONDecoder().decode(HelperConfig.self,
                                           from: Data(#"{"descriptionsEnabled":false}"#.utf8)))?.descriptionsEnabled == false),
        ("精簡及標準候選", namesOnly == "自由。文字。治安"
            && numbersFirst == "1，自由。2，文字。3，治安"
            && candidateAnnouncement([], numbered: true).isEmpty),
        ("Rime 候選頁與單一候選", rimePage?.currentCode == "nH,"
            && rimePage?.candidates == ["你", "您"] && rimePage?.hasOnlyOneCandidate == false
            && single?.hasOnlyOneCandidate == true && empty?.input.isEmpty == true),
        ("Rime 主動報讀後續段落", nextSnapshot?.isSelection == true
            && nextSnapshot?.page.followsSegment(of: firstStage) == true
            && middleStage?.followsSegment(of: firstStage) == true
            && nextSnapshot?.page.followsSegment(of: middleStage) == true
            && nextSnapshot?.page.hasSameCandidateList(as: sameStageUpdate?.page) == true
            && nextSnapshot?.revision != sameStageUpdate?.revision),
        ("實際候選優先", learned == "亞" && mixed == "不過"),
        ("分段選字", remainder?.confirmed == "不過" && remainder?.code == "nH,"
            && remainingComposition(after: fullSelection, in: "不過你", marked: false) == nil
            && inserted == "不過你"),
        ("方案順序", schemaOrder == ["memory", "standard", "single"]
            && fixedOrder == ["standard", "memory", "single"])
    ]
    for (name, ok) in regressions {
        if !ok { pass = false }
        report += "\(name)：\(ok ? "PASS" : "FAIL")\n"
    }
    report += "\n=== self-test \(pass ? "PASS" : "FAIL") ===\n"
    print(report)
    try? report.write(toFile: "/tmp/dimsum_selftest.log", atomically: true, encoding: .utf8)
    exit(pass ? 0 : 1)
}

// 字典自動重載：更新字典腳本（update.sh --pull）改寫 ~/Library/Rime 字典後，
// 助手無需重啟——每 60 秒節流檢查 mtime，變了就重載（組字路徑輕量 stat）
var lastDictReloadCheckAt = Date.distantPast
var dictFileMtime: Date? = nil
func checkDictReload() {
    guard Date().timeIntervalSince(lastDictReloadCheckAt) >= 60 else { return }
    lastDictReloadCheckAt = Date()
    let p = NSHomeDirectory() + "/Library/Rime/dimsum_cb.dict.yaml"
    guard let attrs = try? FileManager.default.attributesOfItem(atPath: p),
          let mtime = attrs[.modificationDate] as? Date else { return }
    if dictFileMtime == nil {
        dictFileMtime = mtime
        return
    }
    if mtime != dictFileMtime {
        dictFileMtime = mtime
        dictReady = false
        dictSortedIdx = []
        dictNormBucket = [:]
        log("偵測到字典已更新（mtime 變更），重載鏡像…")
        loadDimsumDictAsync()
    }
}

/// 以當前輸入碼查詢候選（與引擎同序：權重降序，最多 limit 位）。
/// 引擎語義（dimsum_cb.schema.yaml）：`enable_completion: false` ⇒ 候選清單＝
/// 「碼完全相符」的詞條（不列前綴補全）——鏡像同語義，避免與面板清單不一致。
func dictCandidates(for spelling: String, limit: Int = 9) -> [String] {
    guard dictReady else { return [] }
    let key = dictNormalizeSpelling(spelling)
    guard !key.isEmpty, let idxs = dictNormBucket[key] else { return [] }
    var out: [String] = []
    var seen = Set<String>()
    for i in idxs {
        let w = dictWordArr[i]
        if !seen.contains(w) {
            seen.insert(w)
            out.append(w)
            if out.count >= limit { break }
        }
    }
    return out
}

/// preedit 尾部的零寬分隔標記（U+200B）、換行與溢出空白清除（TTS 不受干擾；
/// 實錄：Rime preedit 會出現「僅含換行符」的幀——不清會令鏡像後備不觸發而靜音）
func cleanPreeditText(_ s: String) -> String {
    s.replacingOccurrences(of: "\u{200B}", with: "")
        .replacingOccurrences(of: "\n", with: "")
        .replacingOccurrences(of: "\r", with: "")
        .trimmingCharacters(in: .whitespacesAndNewlines)
}

/// 是否含 CJK 統一表意文字（Rime 已轉換出漢字＝引擎接受了此碼，非無效編碼）
extension String {
    func containsCJK() -> Bool {
        unicodeScalars.contains { scalar in
            (0x4E00...0x9FFF).contains(scalar.value) || (0x3400...0x4DBF).contains(scalar.value)
        }
    }
}

// ============ 【組字碼重構器】（並擊集合 → 碼字，鏡像 chord_composer） ============
var heldBraille: Set<Int64> = []       // 目前按住中的點字鍵
var burstBraille: Set<Int64> = []      // 本個並擊的整組鍵
var compositionCode = ""               // 自重構的當前輸入碼（與 Rime 一致）

let brailleLetter: [Int64: String] = [1: "s", 2: "d", 3: "f", 38: "j", 40: "k", 37: "l", 0: "a"]
// chordSetMap＝上方的完整反查表（鍵已為排序鍵組，並擊與按下順序無關）

func finalizeBrailleChord() {
    guard heldBraille.isEmpty, !burstBraille.isEmpty else { return }
    let key = burstBraille.compactMap { brailleLetter[$0] }.sorted().joined()
    if let ch = chordSetMap[key] {
        compositionCode.append(ch)
    } else {
        // 未知鍵組記錄供日誌核對（正常情況不應出現——schema 有新規則時同步補表）
        log("並擊反查：未知的鍵組 \"\(key)\"（未計入碼）")
    }
    burstBraille = []
}

func noteBrailleKeyDown(_ code: Int64, isAutoRepeat: Bool) {
    guard !isAutoRepeat else { return }
    heldBraille.insert(code)
    burstBraille.insert(code)
}

func noteBrailleKeyUp(_ code: Int64) {
    guard heldBraille.remove(code) != nil else { return }
    if heldBraille.isEmpty {
        finalizeBrailleChord()
    }
}

func resetCompositionCode() {
    compositionCode = ""
    burstBraille = []
}

/// 【規格第二步：精簡模式穩固切割】回傳「絕不含候選清單」的組字文字。
/// 切割層次：
///   ① 「1.」起點切割（數學確定，見 candidateListStart）
///   ② 候選窗第一位候選文字作錨點切割（清單無編號時）
///   ③ 兜底掃描：殘餘任何「N.」編號一律自最早處去除，並記錄異常供日誌核對
func compositionOnly(_ region: String, winText: String) -> String {
    var base = region
    if let start = candidateListStart(base) {
        base = String(base[..<start])
    }
    let firstCand = candidateText(from: winText, index: 1)
    if !firstCand.isEmpty, firstCand != base,
       let r = base.range(of: firstCand) {
        base = String(base[..<r.lowerBound])
    }
    // ③ 兜底掃描：殘餘「N.」編號標記（正常流程到不了這裡）
    if let m = base.firstMatch(of: #/(\d)\./#) {
        log("切割異常：組字區域殘餘編號標記「\(m.0)」，兜底去除")
        base = String(base[..<m.range.lowerBound])
    }
    return base.trimmingCharacters(in: .whitespaces)
}

/// 從候選窗文字取出第 index 個候選的完整內容
/// 候選窗格式（candidate_format）：「1.你好嗎 2.您好嗎 3.妳好嗎 …」；
/// 若候選窗文字沒有編號，則整段視為第一位候選
func candidateText(from windowText: String, index: Int) -> String {
    guard !windowText.isEmpty else { return "" }
    let all = windowText.matches(of: #/(\d+)\./#)
    var marks: [(num: Int, lower: String.Index, after: String.Index)] = []
    for m in all {
        if let n = Int(m.1), n >= 1, n <= 9 {
            marks.append((n, m.range.lowerBound, m.range.upperBound))
        }
    }
    guard !marks.isEmpty else {
        return index == 1 ? windowText : ""
    }
    guard let mine = marks.firstIndex(where: { $0.num == index }) else { return "" }
    let start = marks[mine].after
    let end: String.Index = (mine + 1 < marks.count) ? marks[mine + 1].lower : windowText.endIndex
    return String(windowText[start..<end]).trimmingCharacters(in: .whitespaces)
}

// ============ 組字／上屏狀態追蹤 ============
var chordAnnounceWork: DispatchWorkItem?
var commitAnnounceWork: DispatchWorkItem?
var candidateIndex = 1
var prevModFlags: CGEventFlags = []
var compositionField: FocusedText?
var compositionPID: pid_t?
var confirmedComposition = ""
var lastCompositionPreview = ""
var lastCandidatePanel = ""
var lastRimeCandidatePage: RimeCandidatePage?
var lastCompositionEvent = Date.distantPast
var navigationWork: DispatchWorkItem?
var rimeReportMonitor: DispatchSourceFileSystemObject?
var observedRimeReport: RimeCandidateReport?
var rimeReportWork: DispatchWorkItem?
var lastSpokenRimePage: RimeCandidatePage?

/// 從組字前的選取位置兩側擷取文字，連在句中插字或取代選取文字也可定位。
func insertedText(before: String, selection: NSRange?, after: String) -> String {
    if let selection, let range = Range(selection, in: before) {
        let prefix = before[..<range.lowerBound]
        let suffix = before[range.upperBound...]
        if after.count >= prefix.count + suffix.count,
           after.hasPrefix(prefix), after.hasSuffix(suffix) {
            return String(after.dropFirst(prefix.count).dropLast(suffix.count))
        }
    }
    return diffField(old: before, new: after).added
}

func currentCompositionText(_ field: FocusedText) -> String {
    guard let baseline = compositionField, CFEqual(baseline.element, field.element) else { return "" }
    let inserted = insertedText(before: baseline.value, selection: baseline.selection, after: field.value)
    if !inserted.isEmpty { return inserted }
    if let marked = field.markedRange, marked.length > 0, let range = Range(marked, in: field.value) {
        return String(field.value[range])
    }
    return ""
}

func activeCompositionText(_ text: String) -> String {
    guard !confirmedComposition.isEmpty, text.hasPrefix(confirmedComposition) else { return text }
    return cleanPreeditText(String(text.dropFirst(confirmedComposition.count)))
}

func startCompositionTracking() {
    compositionPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
    compositionField = getFocusedText()
    confirmedComposition = ""
    lastCompositionPreview = ""
    lastCandidatePanel = ""
    lastRimeCandidatePage = nil
    candidateIndex = 1
    resetCompositionCode()
}

func endCompositionTracking() {
    isComposing = false
    compositionPID = nil
    heldBraille.removeAll()
    compositionField = nil
    lastRimeCandidatePage = nil
    lastSpokenRimePage = nil
    confirmedComposition = ""
    resetCompositionCode()
}

/// 根據已確認的字詞縮短鏡像查表碼。逐字組成和學習候選以實際 inline 文字校驗。
func remainingInputCode(after selected: String, in code: String) -> String? {
    guard !selected.isEmpty, code.count > 1 else { return nil }
    for split in code.indices.dropFirst().reversed() {
        let prefix = String(code[..<split])
        if dictCandidates(for: prefix, limit: Int.max).contains(selected) {
            return String(code[split...])
        }
    }
    return nil
}

func cancelPendingAnnouncements() {
    chordAnnounceWork?.cancel(); chordAnnounceWork = nil
    commitAnnounceWork?.cancel(); commitAnnounceWork = nil
    navigationWork?.cancel(); navigationWork = nil
    rimeReportWork?.cancel(); rimeReportWork = nil
}

func makeSingleCandidatePlayer() -> AVAudioPlayer? {
    guard let url = Bundle.main.url(forResource: "only", withExtension: "wav") else { return nil }
    return try? AVAudioPlayer(contentsOf: url)
}

var singleCandidatePlayer: AVAudioPlayer?

func playSingleCandidateSound() {
    guard let player = singleCandidatePlayer else {
        log("無法載入單一候選音效 only.wav")
        return
    }
    player.currentTime = 0
    if !player.play() { log("無法播放單一候選音效 only.wav") }
}

func speakCandidatePage(_ candidates: [String], hasOnlyOneCandidate: Bool) {
    let text = candidateAnnouncement(candidates, numbered: config.verbosity == 1)
    guard !text.isEmpty else { return }
    stopAnnouncement()
    if hasOnlyOneCandidate { playSingleCandidateSound() }
    announcePreprocessed(text)
}

/// 來自 Rime 報告和按鍵後查詢的同一頁只播一次。
func useRimeCandidatePage(_ page: RimeCandidatePage) {
    chordAnnounceWork?.cancel()
    isComposing = true
    lastRimeCandidatePage = page
    compositionCode = page.currentCode
    candidateIndex = page.highlighted
    lastCandidatePanel = ""
    guard !page.hasSameCandidateList(as: lastSpokenRimePage) else { return }
    lastSpokenRimePage = page
    speakCandidatePage(page.candidates, hasOnlyOneCandidate: page.hasOnlyOneCandidate)
}

func observeRimeCandidateReport() {
    guard let report = readRimeCandidateReport(), report.revision != observedRimeReport?.revision else { return }
    let previous = observedRimeReport?.page
    observedRimeReport = report
    let page = report.page
    guard !page.input.isEmpty else {
        rimeReportWork?.cancel()
        lastSpokenRimePage = nil
        return
    }
    guard !inSettingsMode, schemaMenu == nil, isSquirrelActive(),
          let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier,
          compositionPID == nil || compositionPID == pid else { return }

    let advancesSegment = report.isSelection || page.followsSegment(of: previous)
    // 單純移動高亮由導航報讀處理。組字碼或段落改變才自動讀整頁。
    if !advancesSegment, let previous, page.isSameSegment(as: previous),
       !previous.candidates.isEmpty || page.candidates.isEmpty {
        return
    }

    rimeReportWork?.cancel()
    let work = DispatchWorkItem {
        guard !inSettingsMode, schemaMenu == nil, isSquirrelActive(),
              NSWorkspace.shared.frontmostApplication?.processIdentifier == pid,
              let latest = observedRimeReport?.page, page.isSameSegment(as: latest) else { return }
        if advancesSegment {
            commitAnnounceWork?.cancel()
            pendingSelection = nil
            log("收到 Rime 選字報告：段落起點=\(latest.segmentStart)，候選數=\(latest.candidates.count)")
        } else {
            guard pendingSelection == nil, heldBraille.isEmpty else { return }
        }
        if compositionPID == nil { startCompositionTracking() }
        useRimeCandidatePage(latest)
    }
    rimeReportWork = work
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.09, execute: work)
}

func startRimeReportMonitor() {
    let fd = Darwin.open(rimeReportDirectory(), O_EVTONLY)
    guard fd >= 0 else {
        log("找不到 Rime 候選報告目錄，請執行 deploy-candidate-reporting.sh")
        return
    }
    observedRimeReport = readRimeCandidateReport() // 啟動時跳過上次留下的報告。
    let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: .write, queue: .main)
    source.setEventHandler { observeRimeCandidateReport() }
    source.setCancelHandler { _ = Darwin.close(fd) }
    rimeReportMonitor = source
    source.resume()
}

func scheduleCompositionAnnouncement(recheck: Int = 0, requiresRimeReport: Bool = false) {
    chordAnnounceWork?.cancel()
    let work = DispatchWorkItem {
        guard !inSettingsMode, schemaMenu == nil, heldBraille.isEmpty, isSquirrelActive(),
              NSWorkspace.shared.frontmostApplication?.processIdentifier == compositionPID else { return }

        if let page = readRimeCandidatePage(since: lastCompositionEvent) {
            if page.input.isEmpty {
                endCompositionTracking()
                return
            }
            useRimeCandidatePage(page)
            return
        }
        guard isComposing, !requiresRimeReport else { return }
        checkDictReload()
        let field = getFocusedText()
        if let field, let baseline = compositionField, !CFEqual(field.element, baseline.element) {
            endCompositionTracking()
            return
        }
        let region = field.map { currentCompositionText($0) } ?? ""
        let preview = cleanPreeditText(compositionOnly(region, winText: ""))
        let activePreview = activeCompositionText(preview)
        let panel = candidateListStart(region) != nil ? region : getSquirrelCandidateText(retries: 0) ?? ""
        lastCandidatePanel = panel
        let count = candidateCount(from: panel)

        if (!activePreview.containsCJK() || preview == lastCompositionPreview),
           count == 0, !compositionCode.isEmpty, recheck < 3 {
            scheduleCompositionAnnouncement(recheck: recheck + 1)
            return
        }
        lastCompositionPreview = preview

        let dictionaryCandidates = dictCandidates(for: compositionCode)
        var candidates = dictionaryCandidates
        if count > 0 {
            candidates = (1...9).map { candidateText(from: panel, index: $0) }.filter { !$0.isEmpty }
        } else if activePreview.containsCJK() {
            let first = resolveProgressBase(preeditBase: activePreview, candidates: [], compositionCode: compositionCode)
            candidates.removeAll { $0 == first }
            candidates.insert(first, at: 0)
            candidates = Array(candidates.prefix(9))
        }
        // 沒有引擎報告時，單一候選須有字表與畫面兩個來源互相驗證。
        let visible = count > 0 ? candidates.first
            : resolveProgressBase(preeditBase: activePreview, candidates: [], compositionCode: "")
        let onlyOne = candidates.count == 1 && dictionaryCandidates == candidates && visible == candidates.first
        speakCandidatePage(candidates, hasOnlyOneCandidate: onlyOne)
    }
    chordAnnounceWork = work
    DispatchQueue.main.asyncAfter(deadline: .now() + (recheck == 0 ? 0.20 : 0.12), execute: work)
}

struct CandidateSelection {
    let field: FocusedText?
    let priorPreview: String
    let candidate: String
    let code: String
    let isDigit: Bool
    var eventDate: Date = .distantFuture
    var previousPage: RimeCandidatePage?
}

var pendingSelection: CandidateSelection?

/// 數字鍵可能只確認當前段。使用實際的選字結果分辨剩餘組字與完整上屏。
func remainingComposition(after selection: CandidateSelection, in preview: String,
                          marked: Bool) -> (confirmed: String, code: String)? {
    guard selection.isDigit else { return nil }
    let active = activeCompositionText(preview)
    let before = activeCompositionText(selection.priorPreview)
    let selected = selection.candidate
    if !selected.isEmpty, active.hasPrefix(selected), active.count > selected.count {
        if let code = remainingInputCode(after: selected, in: selection.code) {
            return (selected, code)
        }
        if marked { return (selected, "") }
    }

    // Rime 可在確認一段前後顯示相同的整段 preview，例如「不過你」按 2。
    // AX 若沒有標記範圍，仍可用新一段的文字和編碼一起驗證切點。
    if selected.isEmpty, marked || !before.containsCJK() || active.hasPrefix(before) {
        let wholeCandidates = dictCandidates(for: selection.code)
        if !wholeCandidates.contains(active) {
            for split in active.indices.dropFirst().reversed() {
                let prefix = String(active[..<split])
                let suffix = cleanPreeditText(String(active[split...]))
                if let code = remainingInputCode(after: prefix, in: selection.code),
                   suffix == code || dictCandidates(for: code, limit: Int.max).contains(suffix) {
                    return (prefix, code)
                }
            }
        }
    }
    return nil
}

func finishCandidateSelection() {
    commitAnnounceWork?.cancel()
    commitAnnounceWork = nil
    guard let selection = pendingSelection else { return }
    pendingSelection = nil
    guard NSWorkspace.shared.frontmostApplication?.processIdentifier == compositionPID,
          isSquirrelActive() else {
        endCompositionTracking()
        return
    }
    var page: RimeCandidatePage?
    if let report = readRimeCandidateReport(),
       report.writtenAt >= selection.eventDate ||
           (report.writtenAt.timeIntervalSinceNow >= -2 && report.page.followsSegment(of: selection.previousPage)) {
        page = report.page
    }
    if let page, !page.input.isEmpty {
        useRimeCandidatePage(page)
        log("Rime 選字後仍在組字：段落起點=\(page.segmentStart)，候選數=\(page.candidates.count)")
        return
    }
    guard let current = getFocusedText(),
          compositionField.map({ CFEqual($0.element, current.element) }) ?? true else {
        endCompositionTracking()
        if page?.input.isEmpty == true, config.commitReadEnabled {
            announcePreprocessed(processText(selection.candidate))
        }
        return
    }

    let actual = cleanPreeditText(compositionOnly(currentCompositionText(current), winText: ""))
    let marked = (current.markedRange?.length ?? 0) > 0
    if page == nil, let remaining = remainingComposition(after: selection, in: actual, marked: marked) {
        confirmedComposition += remaining.confirmed
        compositionCode = remaining.code
        lastCandidatePanel = ""
        candidateIndex = 1
        scheduleCompositionAnnouncement()
        log("分段選字：已確認=\(confirmedComposition)，剩餘碼=\(compositionCode)")
        return
    }

    let fieldChange = selection.field.map { diffField(old: $0.value, new: current.value) }
    let hasCandidate = actual.containsCJK() || !selection.candidate.isEmpty || !dictCandidates(for: selection.code).isEmpty
    let invalid = !hasCandidate && !selection.code.isEmpty && actual == selection.code
    endCompositionTracking()
    stopAnnouncement()
    guard config.commitReadEnabled else { return }

    var speech = ""
    if invalid {
        NSSound.beep()
        speech = "錯誤"
    } else if !actual.isEmpty {
        speech = processText(actual)
    } else if let change = fieldChange, !change.added.isEmpty {
        speech = processText(change.added)
    } else if !selection.candidate.isEmpty {
        speech = processText(selection.candidate)
    } else if let change = fieldChange, !change.removed.isEmpty {
        speech = processText("刪除" + change.removed)
    }
    if !speech.isEmpty, config.readWholeLineAfterCommit, !current.value.isEmpty {
        speech += "。整行：" + processText(current.value)
    }
    announcePreprocessed(speech)
}

func scheduleCommitAnnouncement(_ selection: CandidateSelection) {
    pendingSelection = selection
    commitAnnounceWork?.cancel()
    let work = DispatchWorkItem { finishCandidateSelection() }
    commitAnnounceWork = work
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.28, execute: work)
}

func ttsModeName(_ mode: Int) -> String {
    switch mode {
    case 2: return "VoiceOver 整合"
    case 1: return "系統語音"
    default: return "Sin-ji 粵語語音"
    }
}

func verbosityName(_ v: Int) -> String {
    SharedSettingsNames.verbNames.indices.contains(v) ? SharedSettingsNames.verbNames[v] : SharedSettingsNames.verbNames[0]
}

func enterSettingsMode() {
    inSettingsMode = true
    announceSettingText("設定模式。按 1 切換語音模式，按 2 加快語速，按 3 減慢語速，按 4 切換候選用配詞報讀，按 5 切換音量，按 6 切換朗讀內容，按 0 報讀目前設定，按 Escape 退出")
}

func exitSettingsMode() {
    inSettingsMode = false
    saveConfig()
    announceSettingText("退出設定模式，設定已儲存")
}

func handleSettingsKey(_ keyCode: Int64) {
    switch keyCode {
    case 18:
        config.ttsMode = (config.ttsMode + 1) % 3
        announceSettingText("語音模式：" + ttsModeName(config.ttsMode) + (config.ttsMode == 2 && !isVoiceOverRunning() ? "，注意 VoiceOver 尚未執行" : ""))
        saveConfig()
    case 19:
        config.speechRate = min(0.75, config.speechRate + 0.05)
        announceSettingText("語速加快：\(Int(config.speechRate * 100))%")
        saveConfig()
    case 20:
        config.speechRate = max(0.15, config.speechRate - 0.05)
        announceSettingText("語速減慢：\(Int(config.speechRate * 100))%")
        saveConfig()
    case 21:
        config.descriptionsEnabled.toggle()
        announceSettingText("候選用配詞報讀：\(config.descriptionsEnabled ? "開啟" : "關閉")")
        saveConfig()
    case 23:
        if config.volume > 0.5 { config.volume = 0.3; announceSettingText("音量：低") }
        else if config.volume > 0.1 { config.volume = 0.0; announceSettingText("音量：靜音") }
        else { config.volume = 1.0; announceSettingText("音量：高") }
        saveConfig()
    case 22: // 6：朗讀內容（精簡與標準輪流切換）
        config.verbosity = (config.verbosity + 1) % SharedSettingsNames.verbNames.count
        announceSettingText("朗讀內容：" + verbosityName(config.verbosity))
        saveConfig()
    case 29:
        let modeName = ttsModeName(config.ttsMode)
        let descState = config.descriptionsEnabled ? "開啟" : "關閉"
        let voState = isVoiceOverRunning() ? "VoiceOver 執行中" : "VoiceOver 未執行"
        let prefsHkState = config.settingsHotkey.isEmpty ? "偏好設定快捷鍵未啟用" : "偏好設定快捷鍵 \(config.settingsHotkey)"
        let voiceHkState = config.voiceModeHotkey.isEmpty ? "語音模式快捷鍵未啟用" : "語音模式快捷鍵 \(config.voiceModeHotkey)"
        announceSettingText("目前設定：語音模式 \(modeName)，語速 \(Int(config.speechRate * 100))%，候選用配詞報讀 \(descState)，音量 \(Int(config.volume * 100))%，朗讀內容 \(verbosityName(config.verbosity))，\(voState)，\(prefsHkState)，\(voiceHkState)")
    case 53:
        exitSettingsMode()
    default:
        break
    }
}

/// 設定／選單／UI 通知一律走純朗讀通道（跳過字元描述——它們不是候選內容）
func announceSettingText(_ message: String) {
    announce(message, plain: true)
}

// ============ Cleanup ============
func cleanup() {
    stopAnnouncement()
    inSettingsMode = false
    isComposing = false
    log("=== 程式結束 ===")
    logFile?.closeFile()
    exit(0)
}

signal(SIGINT) { _ in cleanup() }
signal(SIGTERM) { _ in cleanup() }
signal(SIGHUP) { _ in cleanup() }

// ============ Daemon Mode ============
func startDaemon() {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: CommandLine.arguments[0])
    var args: [String] = []
    if loggingEnabled { args.append("--log") }
    process.arguments = args
    process.standardInput = FileHandle(forWritingAtPath: "/dev/null")
    process.standardOutput = FileHandle(forWritingAtPath: "/dev/null")
    process.standardError = FileHandle(forWritingAtPath: "/dev/null")
    do {
        try process.run()
        print("點心 VoiceOver 報讀助手已在背景啟動 (PID: \(process.processIdentifier))")
        print("停止方法：./DimsumVoiceHelper --stop")
    } catch {
        print("背景啟動失敗：\(error)")
    }
    exit(0)
}

func stopDaemon() {
    let task = Process()
    task.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
    task.arguments = ["-f", "DimsumVoiceHelper"]
    try? task.run()
    task.waitUntilExit()
    print("點心 VoiceOver 報讀助手已停止")
    exit(0)
}

// ============ Parse Arguments ============
let arguments = CommandLine.arguments
if arguments.contains("--stop") {
    stopDaemon()
} else if arguments.contains("--daemon") || arguments.contains("--background") {
    if arguments.contains("--log") { loggingEnabled = true }
    startDaemon()
} else if arguments.contains("--log") {
    loggingEnabled = true
}

// ============ 選單列狀態圖示 ============
var statusItem: NSStatusItem?

func refreshStatusMenu(_ menu: NSMenu) {
    menu.removeAllItems()
    let title = NSMenuItem(title: "點心語音報讀助手", action: nil, keyEquivalent: "")
    title.isEnabled = false
    menu.addItem(title)
    let vo = NSMenuItem(title: isVoiceOverRunning() ? "VoiceOver：執行中" : "VoiceOver：未執行", action: nil, keyEquivalent: "")
    vo.isEnabled = false
    menu.addItem(vo)
    menu.addItem(.separator())

    let modeTitle = ["Sin-ji 粵語語音", "系統語音", "VoiceOver 整合"]
    for (i, name) in modeTitle.enumerated() {
        let item = NSMenuItem(title: "語音模式：" + name, action: #selector(MenuActions.switchMode(_:)), keyEquivalent: "")
        item.tag = i
        item.state = config.ttsMode == i ? .on : .off
        item.target = menuActions
        menu.addItem(item)
    }
    let verbTitle = ["精簡（候選）", "標準（選字數字及候選）"]
    for (i, name) in verbTitle.enumerated() {
        let item = NSMenuItem(title: "朗讀內容：" + name, action: #selector(MenuActions.switchVerbosity(_:)), keyEquivalent: "")
        item.tag = i
        item.state = config.verbosity == i ? .on : .off
        item.target = menuActions
        menu.addItem(item)
    }
    menu.addItem(.separator())

    let prefs = NSMenuItem(title: "偏好設定…", action: #selector(MenuActions.showPrefs(_:)), keyEquivalent: "")
    prefs.target = menuActions
    menu.addItem(prefs)
    let settings = NSMenuItem(title: "語音設定模式（鍵盤操作）", action: #selector(MenuActions.toggleSettings(_:)), keyEquivalent: "")
    settings.target = menuActions
    menu.addItem(settings)
    let stop = NSMenuItem(title: "立即停止朗讀", action: #selector(MenuActions.stopSpeech(_:)), keyEquivalent: "")
    stop.target = menuActions
    menu.addItem(stop)
    menu.addItem(.separator())
    let quit = NSMenuItem(title: "結束助手", action: #selector(MenuActions.quitHelper(_:)), keyEquivalent: "")
    quit.target = menuActions
    menu.addItem(quit)
}

final class MenuActions: NSObject {
    @objc func showPrefs(_ sender: NSMenuItem) {
        settingsWindowController.showSettings()
    }
    @objc func switchMode(_ sender: NSMenuItem) {
        config.ttsMode = sender.tag
        saveConfig()
        announceSettingText("語音模式：" + ttsModeName(config.ttsMode))
    }
    @objc func switchVerbosity(_ sender: NSMenuItem) {
        config.verbosity = sender.tag
        saveConfig()
        announceSettingText("朗讀內容：" + verbosityName(config.verbosity))
    }
    @objc func toggleSettings(_ sender: NSMenuItem) {
        DispatchQueue.main.async {
            if inSettingsMode { exitSettingsMode() } else { enterSettingsMode() }
        }
    }
    @objc func stopSpeech(_ sender: NSMenuItem) {
        stopAllSpeech()
    }
    @objc func quitHelper(_ sender: NSMenuItem) {
        cleanup()
    }
}
let menuActions = MenuActions()

func setupStatusBar() {
    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    // 喇叭圖示比純文字「點」更易在選單列辨識
    if let img = NSImage(systemSymbolName: "speaker.wave.2.fill", accessibilityDescription: "點心語音報讀助手") {
        item.button?.image = img
        item.button?.image?.size = NSSize(width: 18, height: 18)
    } else {
        item.button?.title = "點"
    }
    item.button?.setAccessibilityLabel("點心語音報讀助手")
    let menu = NSMenu()
    menu.delegate = MenuDelegate.shared
    item.menu = menu
    statusItem = item
}

final class MenuDelegate: NSObject, NSMenuDelegate {
    static let shared = MenuDelegate()
    func menuNeedsUpdate(_ menu: NSMenu) {
        refreshStatusMenu(menu)
    }
}

// ============ 偏好設定視窗（所有設定皆可透過 UI 修改，即時生效並自動儲存） ============
final class SettingsWindowController: NSObject, NSWindowDelegate {
    static let shared = SettingsWindowController()
    private var window: NSWindow?

    func showSettings() {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 470, height: 620),
                             styleMask: [.titled, .closable, .miniaturizable],
                             backing: .buffered, defer: false)
            w.title = "點心語音報讀助手 偏好設定"
            w.delegate = self
            // 【界面統一（round_023）】與管理 app 共用 HelperSettingsForm（Switch 樣式單一真相）；
            // onChange：寫回助手 config 並存檔（即時生效）
            let hosting = NSHostingView(rootView: HelperSettingsForm(
                cfg: Binding(get: { config }, set: { config = $0 }),
                onChange: { saveConfig() }))
            w.contentView = hosting
            window = w
        }
        window?.center()
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func windowWillClose(_ notification: Notification) {
        // 視窗可重複開啟；控制器與視窗保持存活
    }
}

let settingsWindowController = SettingsWindowController()

// ============ 查詢最近報讀的配詞 ============
let lastSpokenLookup = LastSpokenLookup()
var lookupKeyTap: CFMachPort?
var handledLookupKey: Int64?

func requestSpokenPronunciation(from reader: ScreenReaderLookup? = nil) {
    lastSpokenLookup.read(from: reader) { phrase, errorMessage in
        let response = phrase.map { lookupPronunciation($0, using: pronunciations) } ?? errorMessage
        cancelPendingAnnouncements()
        stopAnnouncement()
        // 用助手自身語音回答，讓下一次查詢仍可取得讀屏軟件本來的一句。
        engineBusy = true
        announceSinji(response, plain: true, logText: false)
    }
}

func installLookupKeyTap() {
    let callback: CGEventTapCallBack = { _, type, event, _ in
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap = lookupKeyTap { CGEvent.tapEnable(tap: tap, enable: true) }
            handledLookupKey = nil
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == LastSpokenLookup.copyEventMarker {
            return Unmanaged.passUnretained(event)
        }
        let key = event.getIntegerValueField(.keyboardEventKeycode)
        if handledLookupKey == key {
            if type == .keyUp { handledLookupKey = nil; return nil }
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return nil }
            handledLookupKey = nil
        }
        guard type == .keyDown,
              event.getIntegerValueField(.keyboardEventAutorepeat) == 0,
              HelperShortcut(config.lookupHotkey)?.matches(key, event.flags) == true,
              !inSettingsMode, !NSApp.isActive,
              NSWorkspace.shared.frontmostApplication?.bundleIdentifier != "com.dimsum.braille.manager",
              ![config.settingsHotkey, config.voiceModeHotkey, config.readLineHotkey].contains(where: {
                  HelperShortcut($0)?.matches(key, event.flags) == true
              }) else { return Unmanaged.passUnretained(event) }
        handledLookupKey = key
        DispatchQueue.main.async { requestSpokenPronunciation() }
        return nil
    }
    let keys = CGEventMask((1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue))
    for point: CGEventTapLocation in [.cghidEventTap, .cgSessionEventTap] {
        guard let tap = CGEvent.tapCreate(tap: point, place: .headInsertEventTap, options: .defaultTap,
                                          eventsOfInterest: keys, callback: callback, userInfo: nil) else { continue }
        lookupKeyTap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return
    }
    log("未能啟用查詢最近報讀的快捷鍵。請檢查輔助使用權限。")
}

// ============ Rime 方案選單報讀 ============
struct RimeSchemaOption {
    let id: String
    let name: String
    let lastUsed: Int
}

func rimeSchemaMenuOrder(_ schemas: [RimeSchemaOption], currentID: String?, fixedOrder: Bool) -> [RimeSchemaOption] {
    guard let current = currentID.flatMap({ id in schemas.first { $0.id == id } }) ?? schemas.first else {
        return []
    }
    let others = schemas.filter { $0.id != current.id }
    if fixedOrder { return [current] + others }
    return [current] + others.enumerated().sorted {
        if $0.element.lastUsed != $1.element.lastUsed { return $0.element.lastUsed > $1.element.lastUsed }
        return $0.offset < $1.offset
    }.map(\.element)
}

func readRimeSchemaMenu() -> [RimeSchemaOption] {
    let dir = NSHomeDirectory() + "/Library/Rime"
    let resources = getResourcesDir() + "/rime"
    let deployed = (try? String(contentsOfFile: dir + "/build/default.yaml", encoding: .utf8))
        ?? (try? String(contentsOfFile: resources + "/default.custom.yaml", encoding: .utf8)) ?? ""
    let user = (try? String(contentsOfFile: dir + "/user.yaml", encoding: .utf8)) ?? ""
    let current = user.firstMatch(of: #/previously_selected_schema:\s*"?([\w-]+)"?/#).map { String($0.1) }
    let fixed = deployed.firstMatch(of: #/fix_schema_list_order:\s*true/#) != nil
    let now = Int(Date().timeIntervalSince1970)
    var times: [String: Int] = [:]
    if let accessTimes = user.range(of: "schema_access_time:") {
        let accessLines = user[accessTimes.upperBound...].split(separator: "\n").prefix { $0.hasPrefix("    ") }
        for line in accessLines {
            if let entry = line.firstMatch(of: #/^\s*"?([\w-]+)"?:\s*"?([0-9]+)"?/#),
               let time = Int(entry.2), time <= now {
                times[String(entry.1)] = time
            }
        }
    }
    let schemas = deployed.matches(of: #/(?m)^\s*-\s*\{?\s*schema:\s*"?([\w-]+)"?/#).compactMap { match -> RimeSchemaOption? in
        let id = String(match.1)
        let schema = (try? String(contentsOfFile: dir + "/build/" + id + ".schema.yaml", encoding: .utf8))
            ?? (try? String(contentsOfFile: resources + "/" + id + ".schema.yaml", encoding: .utf8)) ?? ""
        guard let name = schema.firstMatch(of: #/(?m)^  name:\s*([^\r\n]+)/#)?.1 else { return nil }
        let title = name.trimmingCharacters(in: CharacterSet(charactersIn: "\"' \t"))
        return RimeSchemaOption(id: id, name: title, lastUsed: times[id] ?? 0)
    }
    return rimeSchemaMenuOrder(schemas, currentID: current, fixedOrder: fixed)
}

struct RimeSchemaMenu {
    let pid: pid_t
    let field: FocusedText?
    let options: [RimeSchemaOption]
    var index = 0
}

var schemaMenu: RimeSchemaMenu?
var schemaAnnouncement: DispatchWorkItem?

func announceSchemaMenu(opening: Bool) {
    schemaAnnouncement?.cancel()
    guard let menu = schemaMenu else { return }
    let work = DispatchWorkItem {
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier == menu.pid else { return }
        let fallback = menu.options.indices.contains(menu.index) ? menu.options[menu.index].name : ""
        var shown = ""
        if let baseline = menu.field, let field = getFocusedText(), CFEqual(field.element, baseline.element) {
            let inline = cleanPreeditText(insertedText(before: baseline.value, selection: baseline.selection,
                                                       after: field.value))
            // Rime 的方案名稱可直接出現在 inline preview。若 app 不公開它，使用部署清單。
            if menu.options.contains(where: { $0.name == inline }) {
                shown = inline
            }
        }
        let name = shown.isEmpty ? fallback : shown
        let message = opening ? (name.isEmpty ? "方案選單" : "方案選單。\(name)") : name
        announceInterrupt(message, plain: true)
    }
    schemaAnnouncement = work
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16, execute: work)
}

func handleSchemaMenuKey(_ key: Int64, modifiers: CGEventFlags, isRepeat: Bool) -> Bool {
    let opensOrCycles = key == 50 && modifiers == .maskControl
    guard opensOrCycles || schemaMenu != nil else { return false }
    guard let app = NSWorkspace.shared.frontmostApplication,
          (opensOrCycles || schemaMenu?.pid == app.processIdentifier), isSquirrelActive() else {
        schemaAnnouncement?.cancel()
        schemaMenu = nil
        return false
    }

    if opensOrCycles {
        if isRepeat { return true }
        if var menu = schemaMenu {
            if !menu.options.isEmpty { menu.index = (menu.index + 1) % menu.options.count }
            schemaMenu = menu
            announceSchemaMenu(opening: false)
        } else {
            cancelPendingAnnouncements()
            pendingSelection = nil
            endCompositionTracking()
            schemaMenu = RimeSchemaMenu(pid: app.processIdentifier, field: getFocusedText(), options: readRimeSchemaMenu())
            announceSchemaMenu(opening: true)
        }
        return true
    }

    guard var menu = schemaMenu else { return false }
    if !modifiers.isEmpty {
        // 開啟別的 app 或執行 app 快捷鍵時，放下本地選單狀態。
        if modifiers.contains(.maskCommand) {
            schemaAnnouncement?.cancel()
            schemaMenu = nil
        }
        return true
    }

    switch key {
    case 126, 125, 115, 119:  // 上、下、Home、End
        if !menu.options.isEmpty {
            switch key {
            case 126: menu.index = max(0, menu.index - 1)
            case 125: menu.index = min(menu.options.count - 1, menu.index + 1)
            case 115: menu.index = 0
            default: menu.index = menu.options.count - 1
            }
        }
        schemaMenu = menu
        announceSchemaMenu(opening: false)
    case 53, 36, 49, 18, 19, 20, 21, 23, 22, 26, 28, 25:
        schemaAnnouncement?.cancel()
        schemaMenu = nil
    default:
        break
    }
    return true
}

/// 事件監聽是旁聽，Rime 有時已處理事件才到助手。用事件時間對照報告時間。
func keyboardEventDate(_ event: CGEvent) -> Date {
    let now = DispatchTime.now().uptimeNanoseconds
    let stamp = event.timestamp
    let elapsed = stamp <= now ? min(now - stamp, 500_000_000) : 0
    return Date(timeIntervalSinceNow: -Double(elapsed) / 1_000_000_000)
}

func scheduleNavigationAnnouncement(delta: Int, eventDate: Date) {
    chordAnnounceWork?.cancel()
    rimeReportWork?.cancel()
    navigationWork?.cancel()
    let previous = lastRimeCandidatePage
    let fallbackCandidates = previous?.candidates ?? dictCandidates(for: compositionCode)
    let oldIndex = candidateIndex
    candidateIndex = max(1, min(fallbackCandidates.count, candidateIndex + delta))
    let work = DispatchWorkItem {
        guard isComposing, isSquirrelActive(),
              NSWorkspace.shared.frontmostApplication?.processIdentifier == compositionPID else { return }
        if let page = readRimeCandidatePage(since: eventDate),
           previous == nil || page.input == previous?.input,
           page.candidates.indices.contains(page.highlighted - 1) {
            lastRimeCandidatePage = page
            candidateIndex = page.highlighted
            announceInterrupt(processText(page.candidates[page.highlighted - 1], allExamples: true), plain: true)
            return
        }

        // 較舊的 Rime 可能只改高亮位置，沒有發送更新通知。
        let region = getFocusedText().map { currentCompositionText($0) } ?? ""
        let preview = activeCompositionText(cleanPreeditText(compositionOnly(region, winText: "")))
        let visible = resolveProgressBase(preeditBase: preview, candidates: [], compositionCode: compositionCode)
        var text = fallbackCandidates.indices.contains(candidateIndex - 1) ? fallbackCandidates[candidateIndex - 1] : ""
        if visible.containsCJK(), (previous?.segmentStart ?? 0) == 0 { text = visible }
        if text.isEmpty { return }

        let atBoundary = !fallbackCandidates.isEmpty && oldIndex == candidateIndex
        let boundary = delta < 0 ? "已到第一個候選。" : "已到最後一個候選。"
        announceInterrupt((atBoundary ? boundary : "") + processText(text, allExamples: true), plain: true)
    }
    navigationWork = work
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16, execute: work)
}

// ============ Keyboard Event Monitor ============
let keyDownMask = (1 << CGEventType.keyDown.rawValue)
let keyUpMask = (1 << CGEventType.keyUp.rawValue)
let flagsChangedMask = (1 << CGEventType.flagsChanged.rawValue)
// keyUp 必須監聽：組字碼重構器靠「放鍵收束」結算並擊（沒有 keyUp＝碼永遠不成）
let mouseDownMask = (1 << CGEventType.leftMouseDown.rawValue) | (1 << CGEventType.rightMouseDown.rawValue)
let eventMask = CGEventMask(keyDownMask | keyUpMask | flagsChangedMask | mouseDownMask)

// 並擊方案的六個點字鍵和 a 修飾鍵。
let brailleKeys: Set<Int64> = [0, 1, 2, 3, 38, 40, 37]

if CommandLine.arguments.contains("--test-candidate-sound") {
    guard let player = makeSingleCandidatePlayer(), player.play() else {
        print("FAIL: 無法播放 only.wav")
        exit(1)
    }
    RunLoop.current.run(until: Date(timeIntervalSinceNow: player.duration + 0.1))
    exit(0)
}

if CommandLine.arguments.contains("--self-test") {
    guard makeSingleCandidatePlayer() != nil else {
        print("FAIL: 無法載入 only.wav")
        exit(1)
    }
    runSelfTest()
}

guard let eventTap = CGEvent.tapCreate(
    tap: .cgSessionEventTap,
    place: .headInsertEventTap,
    options: .listenOnly,
    eventsOfInterest: eventMask,
    callback: { _, type, event, _ in
        if type == .leftMouseDown || type == .rightMouseDown {
            schemaAnnouncement?.cancel()
            schemaMenu = nil
            cancelPendingAnnouncements()
            pendingSelection = nil
            endCompositionTracking()
            return Unmanaged.passUnretained(event)
        }
        // Ctrl 單鍵「新按下」＝立即停止語音。必須比較前後狀態：
        // 只在「由無 Ctrl 變成單純 Ctrl」時停止——按住 Ctrl+Shift+L 朗讀期間
        // 放開 Shift／L 時 Ctrl 仍在，不能誤判成新的 Ctrl 按下而砍掉整行朗讀
        if type == .flagsChanged {
            let modKeys: CGEventFlags = [.maskControl, .maskShift, .maskAlternate, .maskCommand]
            let nowMods = event.flags.intersection(modKeys)
            if nowMods == .maskControl && !prevModFlags.contains(.maskControl) {
                DispatchQueue.main.async { stopAllSpeech() }
            }
            prevModFlags = nowMods
            return Unmanaged.passUnretained(event)
        }

        guard type == .keyDown || type == .keyUp else { return Unmanaged.passUnretained(event) }

        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        let flags = event.flags
        // 自動重複（長按產生的 keyDown）不觸發任何快捷鍵——
        // 否則朗讀整行會被重複事件不停「停止＋重播」，必須按住才能完成
        let isAutoRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) == 1

        // 組字碼重構：放鍵收束＝並擊結束（與 chord_composer 的收尾一致）；
        // 只在點字鍵上回收，其他鍵不擾動
        if type == .keyUp {
            if heldBraille.contains(keyCode) {
                noteBrailleKeyUp(keyCode)
                if heldBraille.isEmpty { scheduleCompositionAnnouncement() }
            }
            return Unmanaged.passUnretained(event)
        }

        // 兩個快捷鍵（皆預設停用；於偏好設定視窗自訂）：
        //   偏好設定快捷鍵 → 開啟偏好設定視窗
        //   語音設定模式快捷鍵 → 切換語音設定模式（朗讀引導＋數字鍵）
        let relevantFlags = HelperShortcut.modifiers
        if handleSchemaMenuKey(keyCode, modifiers: flags.intersection(relevantFlags), isRepeat: isAutoRepeat) {
            return Unmanaged.passUnretained(event)
        }
        if !isAutoRepeat, HelperShortcut(config.settingsHotkey)?.matches(keyCode, flags) == true {
            DispatchQueue.main.async { settingsWindowController.showSettings() }
            return Unmanaged.passUnretained(event)
        }
        if !isAutoRepeat, HelperShortcut(config.voiceModeHotkey)?.matches(keyCode, flags) == true {
            DispatchQueue.main.async {
                if inSettingsMode { exitSettingsMode() } else { enterSettingsMode() }
            }
            return Unmanaged.passUnretained(event)
        }
        // 朗讀整行快捷鍵：完整重讀的獨立觸發（不在上屏／導航時發生）
        if !isAutoRepeat, HelperShortcut(config.readLineHotkey)?.matches(keyCode, flags) == true {
            DispatchQueue.main.async {
                if let cur = getFocusedTextContent(), !cur.isEmpty {
                    stopAnnouncement()
                    announce(cur)
                }
            }
            return Unmanaged.passUnretained(event)
        }

        // Settings mode
        if inSettingsMode {
            DispatchQueue.main.async { handleSettingsKey(keyCode) }
            return Unmanaged.passUnretained(event)
        }

        let modifiers = flags.intersection(relevantFlags)
        guard !modifiers.contains(.maskControl), !modifiers.contains(.maskCommand),
              !modifiers.contains(.maskAlternate), isSquirrelActive() else {
            return Unmanaged.passUnretained(event)
        }

        if compositionPID != nil,
           NSWorkspace.shared.frontmostApplication?.processIdentifier != compositionPID {
            cancelPendingAnnouncements()
            pendingSelection = nil
            endCompositionTracking()
        }
        if pendingSelection != nil { finishCandidateSelection() }

        let eventDate = keyboardEventDate(event)
        let digitKeyIndex: [Int64: Int] = [18: 1, 19: 2, 20: 3, 21: 4, 23: 5, 22: 6, 26: 7, 28: 8, 25: 9]
        let digit = digitKeyIndex[keyCode]
        let literalSchema = lastRimeCandidatePage?.schema == "dimsum_full"
        let select = keyCode == 49 || (digit != nil && !literalSchema)
        let navigate = keyCode == 125 || keyCode == 126
            || (!literalSchema && (keyCode == 4 || keyCode == 5))

        if keyCode == 53 {
            cancelPendingAnnouncements()
            pendingSelection = nil
            endCompositionTracking()
        } else if select, modifiers.isEmpty, isComposing {
            chordAnnounceWork?.cancel()
            navigationWork?.cancel()
            finalizeBrailleChord()

            let field = getFocusedText()
            let region = field.map { currentCompositionText($0) } ?? ""
            let preview = cleanPreeditText(compositionOnly(region, winText: ""))
            let index = digit ?? candidateIndex
            let panel = candidateListStart(region) != nil ? region : lastCandidatePanel
            let knownCandidates = lastRimeCandidatePage?.candidates ?? []
            var selected = knownCandidates.indices.contains(index - 1) ? knownCandidates[index - 1] : ""
            if selected.isEmpty, candidateCount(from: panel) > 0 {
                selected = candidateText(from: panel, index: index)
            }
            if selected.isEmpty, index == candidateIndex {
                selected = resolveProgressBase(preeditBase: activeCompositionText(preview),
                                               candidates: [], compositionCode: compositionCode)
                if !selected.containsCJK() { selected = "" }
            }
            scheduleCommitAnnouncement(CandidateSelection(field: field, priorPreview: preview, candidate: selected,
                                                           code: compositionCode, isDigit: digit != nil, eventDate: eventDate,
                                                           previousPage: lastRimeCandidatePage))
        } else if navigate, modifiers.isEmpty, isComposing {
            let delta = (keyCode == 125 || keyCode == 4) ? 1 : -1
            scheduleNavigationAnnouncement(delta: delta, eventDate: eventDate)
        } else if keyCode == 51, isComposing {
            navigationWork?.cancel()
            lastCompositionEvent = eventDate
            if !compositionCode.isEmpty { compositionCode.removeLast() }
            scheduleCompositionAnnouncement()
        } else if brailleKeys.contains(keyCode), modifiers.isEmpty, !literalSchema, !isAutoRepeat {
            navigationWork?.cancel()
            chordAnnounceWork?.cancel()
            if !isComposing {
                startCompositionTracking()
                isComposing = true
            }
            lastCompositionEvent = eventDate
            noteBrailleKeyDown(keyCode, isAutoRepeat: false)
            candidateIndex = 1
            lastCandidatePanel = ""
        } else if keyCode <= 50, keyCode != 49, keyCode != 36,
                  heldBraille.isEmpty, !isAutoRepeat {
            // 一陽指模式可直接輸入普通鍵，由 Rime 報告實際候選。
            // 在其他輸入方案中，只有收到本次按鍵的 Rime 報告才會出聲。
            navigationWork?.cancel()
            if !isComposing, compositionPID == nil { startCompositionTracking() }
            lastCompositionEvent = eventDate
            scheduleCompositionAnnouncement(requiresRimeReport: true)
        }

        return Unmanaged.passUnretained(event)
    },
    userInfo: nil
) else {
    print("ERROR: 無法建立事件監聯。請在 系統設定 > 隱私與安全 > 輔助使用 中授予權限")
    exit(1)
}

let runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, eventTap, 0)
CFRunLoopAddSource(CFRunLoopGetCurrent(), runLoopSource, .commonModes)
CGEvent.tapEnable(tap: eventTap, enable: true)

// ============ 主執行緒：App 環境（選單列）＋初始化 ============
let app = NSApplication.shared
// .accessory：不出現在 Dock，但會出現在 Cmd+Tab 切換器——
// 用戶可用 Cmd+Tab → Cmd+` 到達偏好設定視窗
app.setActivationPolicy(.accessory)

setupStatusBar()

initLogging()
loadConfigFromFile()
applyLoggingState()   // 管理器「開啟/關閉日誌記錄」設定即時套用（config 持久化，重啟後仍生效）
saveConfig()  // Save config on startup (creates directory + file)
loadPronunciations()
singleCandidatePlayer = makeSingleCandidatePlayer()
startRimeReportMonitor()
loadDimsumDictAsync()   // 【字典鏡像引擎】背景載入 dimsum_cb 字表（v1 清單／v2 總數／選字定位供值）
speechSynthesizer.delegate = speechDelegate
installLookupKeyTap()
// 語音引擎預熱：Sin-ji 語音載入需要 1–2 秒，以靜音 utterance 預先喚醒，
// 避免助手啟動後第一次打字卡住、沒有聲音
DispatchQueue.main.async {
    _ = AVSpeechSynthesisVoice.speechVoices()
    func warmup() {
        let u = AVSpeechUtterance(string: "點心")
        if let v = AVSpeechSynthesisVoice(identifier: "com.apple.voice.compact.zh-HK.Sinji") { u.voice = v }
        u.volume = 0.05
        speechSynthesizer.speak(u)
    }
    warmup()
    // 再預熱兩次，確保引擎完成初始化（之後使用者打第一個字即能朗讀）
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { warmup() }
    DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) { warmup() }
}

var tccObservers: [NSObjectProtocol] = []   // block 型 observer 的 token 必須持有

// 讀屏軟件開啟背景查配詞指令，通知只帶讀屏軟件名稱。
tccObservers.append(DistributedNotificationCenter.default().addObserver(
    forName: ScreenReaderLookup.request, object: nil, queue: .main
) { notification in
    guard let name = notification.object as? String, let reader = ScreenReaderLookup(rawValue: name) else { return }
    requestSpokenPronunciation(from: reader)
})

// dimsum_tool show-helper-prefs 廣播此通知以開啟偏好設定視窗。
tccObservers.append(DistributedNotificationCenter.default().addObserver(
    forName: Notification.Name("com.dimsum.braille.show-helper-prefs"),
    object: nil, queue: .main
) { _ in
    settingsWindowController.showSettings()
})
// 管理器內的設定視窗修改設定後 → 廣播此通知 → 助手即時重新載入設定
tccObservers.append(DistributedNotificationCenter.default().addObserver(
    forName: Notification.Name("com.dimsum.braille.reload-config"),
    object: nil, queue: .main
) { _ in
    loadConfigFromFile()
    applyLoggingState()   // 管理器切換「開啟/關閉日誌記錄」即時生效
    print("配置已重新載入（管理器修改）")
    log("配置已重新載入（管理器修改）: ttsMode=\(config.ttsMode), verbosity=\(config.verbosity)")
})

print("================================================")
print("  點心語音報讀助手 V\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.2.1")")
print("  含 NVDA 字典發音表（\(pronunciations.count) 條）")
print("================================================")
print("")
print("語音模式：\(ttsModeName(config.ttsMode))")
print("朗讀內容：\(verbosityName(config.verbosity))")
print("語速：\(Int(config.speechRate * 100))%")
print("候選用配詞報讀：\(config.descriptionsEnabled ? "開啟" : "關閉")")
print("選單列「點」圖示 →「偏好設定…」可修改所有設定（語音模式／朗讀內容／")
print("語速／音量／候選用配詞報讀／設定模式快捷鍵），修改即時生效並自動儲存")
print("")
print("候選導航：")
print("  Down / H — 往下瀏覽（報讀候選）")
print("  Up / G   — 往上瀏覽（報讀候選）")
print("  Space    — 上字（即時朗讀新上屏的字）")
print("  1-9      — 快速選字（即時朗讀新上屏的字）")
print("  Ctrl     — 立即停止所有語音")
print("")
if config.settingsHotkey.isEmpty && config.voiceModeHotkey.isEmpty {
    print("快捷鍵：預設全部停用（偏好設定快捷鍵／語音設定模式快捷鍵）")
    print("  如需快捷鍵：於「偏好設定…」視窗中輸入即可")
} else {
    print("偏好設定快捷鍵：\(config.settingsHotkey.isEmpty ? "（停用）" : config.settingsHotkey)")
    print("語音設定模式快捷鍵：\(config.voiceModeHotkey.isEmpty ? "（停用）" : config.voiceModeHotkey)")
}
print("")
print("語音設定模式（選單列「點」→「語音設定模式」，朗讀引導＋數字鍵）：")
print("  1 — 切換語音模式（Sin-ji ↔ 系統語音 ↔ VoiceOver 整合）")
print("  2 — 加快語速")
print("  3 — 減慢語速")
print("  4 — 切換候選用配詞報讀")
print("  5 — 切換音量（高→低→靜音→高）")
print("  6 — 切換朗讀內容（精簡 ↔ 標準）")
print("  0 — 報讀目前設定")
print("  Esc — 退出設定模式（自動儲存）")
print("")
print("設定檔：\(getConfigPath())")
if loggingEnabled {
    print("日誌檔：\(getLogPath())")
}
print("")

// NSApp.run 驅動完整 AppKit 事件處理（視窗、選單列、事件監聽都在此運行）；
// 舊的 CFRunLoopRun 不處理 AppKit 事件，會令偏好設定視窗一開啟就凍結
NSApp.run()
