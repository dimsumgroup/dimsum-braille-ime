// ============================================================================
// dimsum_tool — 點心粵語輸入法命令列輔助工具
//
// 編譯後內嵌於 DimsumManager.app/Contents/Resources/，
// 取代執行期對 Xcode CLT（swift / python3）的依賴：
// 新 Mac 未安裝 CLT 時，所有功能仍可正常運作。
//
// 用法：dimsum_tool <子命令> [參數]
//   enable-squirrel                          啟用所有 Squirrel 輸入法來源（TIS）
//   disable-squirrel                         停用所有 Squirrel 輸入法來源（TIS）
//   reload-squirrel                          廣播 SquirrelReloadNotification
//   clean-default-custom                     清除 ~/Library/Rime/default.custom.yaml 中的 dimsum 方案條目
//   convert-voiceover <nvda_dic_dir> <out>   NVDA 字典 → VoiceOver 發音表（JSON/CSV/Swift）
// ============================================================================
import Foundation
import Carbon
import AppKit

// MARK: - TIS 輸入法來源

func squirrelInputSources() -> [TISInputSource] {
    let list = TISCreateInputSourceList(nil, true)?.takeRetainedValue() as? [TISInputSource] ?? []
    return list.filter { source in
        guard let idPtr = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return false }
        let id = Unmanaged<CFString>.fromOpaque(idPtr).takeUnretainedValue() as String
        return id.contains("Squirrel")
    }
}

func enableSquirrel() -> Int32 {
    let sources = squirrelInputSources()
    guard !sources.isEmpty else {
        print("✗ 系統輸入法清單中找不到 Squirrel")
        print("  若剛完成安裝，請登出再登入（或重新開機）後重試")
        return 1
    }
    var enabled = 0
    for source in sources where TISEnableInputSource(source) == 0 { enabled += 1 }
    print("已啟用 \(enabled) 個 Squirrel 輸入法來源")
    return enabled > 0 ? 0 : 1
}

func disableSquirrel() -> Int32 {
    let sources = squirrelInputSources()
    guard !sources.isEmpty else { print("未找到 Squirrel 輸入法來源，無需停用"); return 0 }
    for source in sources { TISDisableInputSource(source) }
    print("已停用 \(sources.count) 個 Squirrel 輸入法來源")
    return 0
}

// MARK: - Squirrel 重新載入

func reloadSquirrel() -> Int32 {
    let center = DistributedNotificationCenter.default()
    center.postNotificationName(Notification.Name("SquirrelReloadNotification"),
                                object: nil, userInfo: nil, deliverImmediately: true)
    print("已廣播 SquirrelReloadNotification")
    return 0
}

// MARK: - default.custom.yaml 清理

func cleanDefaultCustom() -> Int32 {
    let path = NSHomeDirectory() + "/Library/Rime/default.custom.yaml"
    guard FileManager.default.fileExists(atPath: path) else {
        print("找不到 \(path)，無需清理")
        return 0
    }
    guard let content = try? String(contentsOfFile: path, encoding: .utf8) else {
        print("✗ 無法讀取 \(path)")
        return 1
    }
    var removed = 0
    let lines = content.components(separatedBy: "\n")
    let kept = lines.filter { line in
        let isDimsumEntry = line.contains("{schema: dimsum_") || line.contains("{schema:dimsum_")
        if isDimsumEntry { removed += 1 }
        return !isDimsumEntry
    }
    guard removed > 0 else { print("default.custom.yaml 中沒有 dimsum 方案條目"); return 0 }
    let result = kept.joined(separator: "\n")
    do {
        try result.write(toFile: path, atomically: true, encoding: .utf8)
        print("已從 default.custom.yaml 移除 \(removed) 個 dimsum 方案條目")
        return 0
    } catch {
        print("✗ 寫入 \(path) 失敗：\(error)")
        return 1
    }
}

// MARK: - NVRA 字典 → VoiceOver 發音表轉換

/// 以 UTF-8 位元組為鍵的字典鍵。
/// Swift 的 String 相等比較採 Unicode 規範等價，會把相容表意文字（U+F900 區段）與其
/// 標準字（如 U+F900 與 U+8C48）合併為同一鍵，導致條目丟失；以原始位元組為鍵可與
/// Python（碼點精確）的轉換結果完全一致。
struct CpKey: Hashable, Comparable {
    var bytes: [UInt8]
    static func == (a: CpKey, b: CpKey) -> Bool { a.bytes == b.bytes }
    func hash(into h: inout Hasher) { h.combine(bytes) }
    static func < (a: CpKey, b: CpKey) -> Bool {
        for (x, y) in zip(a.bytes, b.bytes) where x != y { return x < y }
        return a.bytes.count < b.bytes.count
    }
    var asString: String { String(decoding: bytes, as: UTF8.self) }
    init(_ s: String) { bytes = Array(s.utf8) }
}

extension String {
    var allASCII: Bool { unicodeScalars.allSatisfy { $0.isASCII } }
}

/// 讀取文字檔：去除 UTF-8 BOM，並將 CRLF／CR 統一為 LF（等同 Python 文字模式的 universal newlines）
func readTextFile(_ path: String) -> String? {
    guard let raw = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
    var text = raw.hasPrefix("\u{FEFF}") ? String(raw.dropFirst()) : raw
    text = text.replacingOccurrences(of: "\r\n", with: "\n")
               .replacingOccurrences(of: "\r", with: "\n")
    return text
}

/// 解析 characterDescriptions.dic：字元 \t 描述1 \t 描述2 …
func parseCharacterDescriptions(_ path: String) -> [CpKey: String] {
    var entries: [CpKey: String] = [:]
    guard let text = readTextFile(path) else { return entries }
    for line in text.components(separatedBy: "\n") {
        if line.isEmpty || line.unicodeScalars.first == "#" { continue }
        let parts = line.components(separatedBy: "\t")
        guard parts.count >= 2 else { continue }
        let character = parts[0]
        if character.count == 1, character.allASCII, character.first?.isLetter == true { continue }
        if character.count > 10 { continue }
        entries[CpKey(character)] = parts[1...].joined(separator: " ")
    }
    return entries
}

/// 解析 symbols.dic：symbols: 區段內 識別符 \t 替換 \t 層級 \t 保留
func parseSymbols(_ path: String) -> [CpKey: String] {
    var entries: [CpKey: String] = [:]
    guard let text = readTextFile(path) else { return entries }
    var inSymbols = false
    for line in text.components(separatedBy: "\n") {
        if line.hasPrefix("complexSymbols:") { inSymbols = false; continue }
        if line.hasPrefix("symbols:") { inSymbols = true; continue }
        guard inSymbols else { continue }
        if line.isEmpty || line.unicodeScalars.first == "#" { continue }
        let parts = line.components(separatedBy: "\t")
        guard parts.count >= 2, !parts[1].isEmpty else { continue }
        let identifier = parts[0]
        if identifier.contains(" ") { continue }                       // 多詞識別符（正則）
        if identifier.hasPrefix("\\"), identifier.count == 2 { continue } // 跳脫序列 \t \n 等
        if identifier.count > 10 { continue }
        entries[CpKey(identifier)] = parts[1]
    }
    return entries
}

/// 解析 cldr.dic：symbols: 區段內 字元 \t 描述 \t 層級
func parseCldr(_ path: String) -> [CpKey: String] {
    var entries: [CpKey: String] = [:]
    guard let text = readTextFile(path) else { return entries }
    var inSymbols = false
    for line in text.components(separatedBy: "\n") {
        if line.hasPrefix("symbols:") { inSymbols = true; continue }
        guard inSymbols else { continue }
        if line.isEmpty || line.unicodeScalars.first == "#" { continue }
        let parts = line.components(separatedBy: "\t")
        guard parts.count >= 2, !parts[1].isEmpty else { continue }
        if parts[0].count > 10 { continue }
        entries[CpKey(parts[0])] = parts[1]
    }
    return entries
}

/// 合併三個字典，優先級：charDesc > symbols > cldr
func mergeDictionaries(_ charDesc: [CpKey: String], _ symbols: [CpKey: String], _ cldr: [CpKey: String]) -> [CpKey: String] {
    var merged = cldr
    for (k, v) in symbols { merged[k] = v }
    for (k, v) in charDesc { merged[k] = v }
    return merged
}

func writeJSON(_ entries: [CpKey: String], to path: String) -> Int {
    // 手工序列化（Python json.dump indent=0、ensure_ascii=False、sort_keys 風格），
    // 保留相容表意文字等原始碼點，不受 Swift 規範等價影響
    func jsonEscape(_ s: String) -> String {
        var out = ""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 { out += String(format: "\\u%04x", scalar.value) }
                else { out += String(scalar) }
            }
        }
        return out
    }
    var out = "{"
    var first = true
    for (key, value) in entries.sorted(by: { $0.key < $1.key }) {
        if !first { out += "," }
        first = false
        out += "\n\"\(jsonEscape(key.asString))\": \"\(jsonEscape(value))\""
    }
    out += "\n}\n"
    do {
        try out.write(toFile: path, atomically: true, encoding: .utf8)
        print("  JSON 發音表：\(path)（\(entries.count) 條）")
        return 0
    } catch {
        print("✗ 寫入 \(path) 失敗：\(error)"); return 1
    }
}

func csvEscape(_ field: String) -> String {
    if field.contains(",") || field.contains("\"") || field.contains("\n") || field.contains("\r") {
        return "\"" + field.replacingOccurrences(of: "\"", with: "\"\"") + "\""
    }
    return field
}

func writeCSV(_ entries: [CpKey: String], to path: String) -> Int {
    var out = "character,replacement\n"
    for (char, repl) in entries.sorted(by: { $0.key < $1.key }) {
        out += "\(csvEscape(char.asString)),\(csvEscape(repl))\n"
    }
    do {
        try out.write(toFile: path, atomically: true, encoding: .utf8)
        print("  CSV 中介檔：\(path)（\(entries.count) 條）")
        return 0
    } catch {
        print("✗ 寫入 \(path) 失敗：\(error)"); return 1
    }
}

func swiftEscape(_ s: String) -> String {
    s.replacingOccurrences(of: "\\", with: "\\\\")
     .replacingOccurrences(of: "\"", with: "\\\"")
     .replacingOccurrences(of: "\n", with: "\\n")
     .replacingOccurrences(of: "\r", with: "\\r")
}

func writeSwiftPronunciations(_ entries: [CpKey: String], to path: String) -> Int {
    var out = "// 點心粵語點字輸入法 — VoiceOver 發音表\n"
    out += "// 由 NVDA 字典自動轉換生成（characterDescriptions.dic + symbols.dic + cldr.dic）\n"
    out += "// 共 \(entries.count) 條發音規則\n\n"
    out += "import Foundation\n\n"
    out += "let dimsumPronunciations: [String: String] = [\n"
    for (char, repl) in entries.sorted(by: { $0.key < $1.key }) {
        var value = swiftEscape(repl)
        if value.count > 200 { value = String(value.prefix(200)) + "..." }
        out += "    \"\(swiftEscape(char.asString))\": \"\(value)\",\n"
    }
    out += "]\n"
    do {
        try out.write(toFile: path, atomically: true, encoding: .utf8)
        print("  Swift 發音表：\(path)（\(entries.count) 條）")
        return 0
    } catch {
        print("✗ 寫入 \(path) 失敗：\(error)"); return 1
    }
}

func convertVoiceover(nvdaDir: String, outputDir: String) -> Int32 {
    print(String(repeating: "=", count: 60))
    print("  NVDA 字典 → VoiceOver 發音表轉換工具")
    print(String(repeating: "=", count: 60))

    let charDescPath = nvdaDir + "/characterDescriptions.dic"
    let symbolsPath = nvdaDir + "/symbols.dic"
    let cldrPath = nvdaDir + "/cldr.dic"
    for p in [charDescPath, symbolsPath, cldrPath] {
        guard FileManager.default.fileExists(atPath: p) else {
            print("✗ 找不到 \(p)")
            print("  請先執行 update_accessibility.sh 同步 NVDA 字典")
            return 1
        }
    }

    print("\n① 解析 characterDescriptions.dic……")
    let charDesc = parseCharacterDescriptions(charDescPath)
    print("   → \(charDesc.count) 條")

    print("② 解析 symbols.dic……")
    let symbols = parseSymbols(symbolsPath)
    print("   → \(symbols.count) 條")

    print("③ 解析 cldr.dic……")
    let cldr = parseCldr(cldrPath)
    print("   → \(cldr.count) 條")

    print("④ 合併（優先級：charDesc > symbols > cldr）……")
    let merged = mergeDictionaries(charDesc, symbols, cldr)
    print("   → 合併後共 \(merged.count) 條唯一條目")

    do { try FileManager.default.createDirectory(atPath: outputDir, withIntermediateDirectories: true) } catch {
        print("✗ 無法建立輸出目錄 \(outputDir)：\(error)"); return 1
    }
    print("\n⑤ 生成發音表檔案……")
    var rc = 0
    rc |= writeJSON(merged, to: outputDir + "/DimsumPronunciations.json")
    rc |= writeCSV(merged, to: outputDir + "/voiceover_pronunciations.csv")
    rc |= writeSwiftPronunciations(merged, to: outputDir + "/DimsumPronunciations.swift")
    if rc == 0 { print("\n✓ 轉換完成！") }
    return Int32(rc)
}

// MARK: - Squirrel 候選窗無障礙結構探測（診斷用）

/// 列出 AX 元素的所有屬性名與取值（診斷候選文字位置用）
func dumpAXAttributes(_ el: AXUIElement, depth: Int, out: NSMutableString) {
    var namesRef: CFArray?
    AXUIElementCopyAttributeNames(el, &namesRef)
    guard let names = namesRef as? [String] else { return }
    for n in names {
        var vRef: CFTypeRef?
        AXUIElementCopyAttributeValue(el, n as CFString, &vRef)
        if let v = vRef {
            let shown = String(describing: v).prefix(90)
            out.append(String(repeating: "  ", count: depth) + "· \(n) = \(shown)\n")
        }
    }
}

/// 逐層列出 AX 元素的角色與數值
func dumpAXElement(_ el: AXUIElement, depth: Int, out: NSMutableString) {
    guard depth <= 6, out.length < 60000 else { return }
    var roleRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXRoleAttribute as CFString, &roleRef)
    var valRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXValueAttribute as CFString, &valRef)
    var titleRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXTitleAttribute as CFString, &titleRef)
    let role = (roleRef as? String) ?? "?"
    let title = (titleRef as? String) ?? ""
    let val = (valRef as? String) ?? ""
    var desc = String(repeating: "  ", count: depth) + role
    if !title.isEmpty { desc += " title=\(title.prefix(60))" }
    if !val.isEmpty { desc += " value=\(val.prefix(120))" }
    out.append(desc + "\n")
    var kidsRef: CFTypeRef?
    AXUIElementCopyAttributeValue(el, kAXChildrenAttribute as CFString, &kidsRef)
    if let kids = kidsRef as? [AXUIElement] {
        for k in kids { dumpAXElement(k, depth: depth + 1, out: out) }
    }
}

func probeSquirrelAX() -> Int32 {
    print("建立自建文字框並注入點字鍵序列……")
    let win = NSWindow(contentRect: NSRect(x: 200, y: 500, width: 600, height: 160),
                       styleMask: [.titled], backing: .buffered, defer: false)
    win.title = "點心診斷視窗"
    let field = NSTextField(frame: NSRect(x: 20, y: 60, width: 560, height: 30))
    field.placeholderString = "（診斷用，稍後自動關閉）"
    win.contentView?.addSubview(field)
    NSApp.setActivationPolicy(.accessory)
    NSApp.activate(ignoringOtherApps: true)
    win.makeKeyAndOrderFront(nil)
    field.becomeFirstResponder()
    win.makeFirstResponder(field)

    func inject(_ code: CGKeyCode, down: Bool) {
        let ev = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)
        ev?.post(tap: .cghidEventTap)
        usleep(12000)
    }
    DispatchQueue.global().asyncAfter(deadline: .now() + 0.6) {
        // 注入一組點字並擊（取自實機日誌的鍵序）
        for kc in [3, 2, 38, 37, 1, 2, 37, 3, 38, 2, 3] {
            inject(CGKeyCode(kc), down: true)
            inject(CGKeyCode(kc), down: false)
            usleep(60000)
        }
        usleep(400000)
        // 自我檢查：注入是否真的進入本視窗的文字框
        let fieldValue = field.stringValue
        // 輪詢 Squirrel 視窗（組字存在的短暫時間內多次嘗試）
        let out = NSMutableString()
        out.append("注入後自建文字框值：\"\(fieldValue)\"（字數 \(fieldValue.count)）\n")
        guard let sq = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == "im.rime.inputmethod.Squirrel" || ($0.localizedName ?? "").contains("Squirrel") }) else {
            out.append("找不到 Squirrel 進程\n")
            try? out.write(toFile: "/tmp/squirrel_ax.log", atomically: true, encoding: String.Encoding.utf8.rawValue)
            print(out as String); DispatchQueue.main.async { exit(1) }
            return
        }
        out.append("找到 Squirrel PID=\(sq.processIdentifier) name=\(sq.localizedName ?? "")\n")
        let axApp = AXUIElementCreateApplication(sq.processIdentifier)
        for attempt in 1...20 {
            var winsRef: CFTypeRef?
            AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &winsRef)
            if let wins = winsRef as? [AXUIElement], !wins.isEmpty {
                out.append("第 \(attempt) 次輪詢：windows 數量 \(wins.count)\n")
                if let w = wins.first {
                    dumpAXAttributes(w, depth: 0, out: out)
                    var kidsRef: CFTypeRef?
                    AXUIElementCopyAttributeValue(w, kAXChildrenAttribute as CFString, &kidsRef)
                    if let kids = kidsRef as? [AXUIElement] {
                        for k in kids { dumpAXAttributes(k, depth: 1, out: out) }
                    }
                }
                break
            }
            usleep(150000)
            if attempt == 20 { out.append("20 次輪詢皆無 windows（組字未發生或視窗不可列出）\n") }
        }
        try? out.write(toFile: "/tmp/squirrel_ax.log", atomically: true, encoding: String.Encoding.utf8.rawValue)
        print("探測完成，結果已寫入 /tmp/squirrel_ax.log（前 60 行）：")
        let lines = (out as String).components(separatedBy: "\n").prefix(60).joined(separator: "\n")
        print(lines)
        DispatchQueue.main.async { exit(0) }
    }
    NSApp.run()
    return 0
}

// MARK: - 主入口

let args = CommandLine.arguments
guard args.count >= 2 else {
    print("用法：dimsum_tool <子命令> [參數]")
    print("  enable-squirrel | disable-squirrel | reload-squirrel | clean-default-custom")
    print("  convert-voiceover <nvda_dic_dir> <output_dir>")
    print("  show-helper-prefs  廣播通知，令報讀助手開啟偏好設定視窗")
    exit(2)
}

let rc: Int32
switch args[1] {
case "enable-squirrel":       rc = enableSquirrel()
case "disable-squirrel":      rc = disableSquirrel()
case "reload-squirrel":       rc = reloadSquirrel()
case "clean-default-custom":  rc = cleanDefaultCustom()
case "show-helper-prefs":
    DistributedNotificationCenter.default().postNotificationName(
        Notification.Name("com.dimsum.braille.show-helper-prefs"),
        object: nil, userInfo: nil, deliverImmediately: true)
    print("已通知報讀助手開啟偏好設定")
    rc = 0
case "probe-squirrel":
    NSApplication.shared
    rc = probeSquirrelAX()
case "convert-voiceover":
    guard args.count >= 4 else { print("用法：dimsum_tool convert-voiceover <nvda_dic_dir> <output_dir>"); exit(2) }
    rc = convertVoiceover(nvdaDir: args[2], outputDir: args[3])
default:
    print("未知子命令：\(args[1])")
    exit(2)
}
exit(rc)
