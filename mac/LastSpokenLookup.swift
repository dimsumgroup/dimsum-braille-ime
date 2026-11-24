import AppKit
import Carbon.HIToolbox
import CoreGraphics

func pronunciationText(_ text: String, using entries: [String: String], allExamples: Bool) -> String {
    text.map { character in
        let original = String(character)
        guard let description = entries[original] else { return original }
        // 空格分隔各組配詞，同組內的逗號和別稱一併保留。
        let firstGroup = description.split(separator: " ", maxSplits: 1).first.map { String($0) }
        return (allExamples ? description : (firstGroup ?? description)) + "。"
    }.joined()
}

func lookupPronunciation(_ phrase: String, using entries: [String: String]) -> String {
    let text = phrase.trimmingCharacters(in: .whitespacesAndNewlines)
    guard !text.isEmpty else { return "讀屏軟件還沒有可查詢的內容。" }
    guard text.contains(where: { entries[String($0)] != nil }) else { return "沒有找到配詞解釋。" }
    return pronunciationText(text, using: entries, allExamples: text.count == 1)
}

/// On demand only. TurnOver has a copy command but no direct interface for reading its last phrase.
final class LastSpokenLookup {
    static let copyEventMarker: Int64 = 0x44494D53
    private var inProgress = false

    func read(from reader: ScreenReaderLookup?, reply: @escaping (String?, String) -> Void) {
        guard !inProgress else { return }
        guard !IsSecureEventInputEnabled() else { return }
        inProgress = true
        let finish: (String?, String) -> Void = { text, message in
            self.inProgress = false
            reply(text, message)
        }
        let turnOverRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: "com.turnover.reader").isEmpty
        let voiceOverRunning = NSWorkspace.shared.isVoiceOverEnabled
        let active = reader ?? (turnOverRunning ? .turnOver : (voiceOverRunning ? .voiceOver : nil))
        switch active {
        case .voiceOver? where voiceOverRunning: readVoiceOver(reply: finish)
        case .turnOver? where turnOverRunning: readUsingCopy(reply: finish)
        case .voiceOver?: finish(nil, "請先啟動 VoiceOver。")
        case .turnOver?: finish(nil, "請先啟動 TurnOver。")
        case nil: finish(nil, "請先啟動 VoiceOver 或 TurnOver。")
        }
    }

    private func readVoiceOver(reply: @escaping (String?, String) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            process.arguments = ["-e", """
                with timeout of 5 seconds
                    tell application "VoiceOver" to return content of last phrase
                end timeout
                """]
            let output = Pipe()
            process.standardOutput = output
            process.standardError = FileHandle.nullDevice
            var phrase: String?
            if (try? process.run()) != nil {
                let data = output.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                if process.terminationStatus == 0 { phrase = String(data: data, encoding: .utf8) }
            }
            let result = phrase
            DispatchQueue.main.async {
                reply(result, "未能取得 VoiceOver 的報讀內容。請檢查 VoiceOver 工具程式的 AppleScript 設定及 Mac 的自動化權限。")
            }
        }
    }

    private func readUsingCopy(reply: @escaping (String?, String) -> Void) {
        guard CGPreflightPostEventAccess(),
              let source = CGEventSource(stateID: .privateState),
              let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_C), keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(kVK_ANSI_C), keyDown: false) else {
            reply(nil, "請在 Mac 的輔助使用設定允許點心語音報讀助手。")
            return
        }
        let pasteboard = NSPasteboard.general
        let initialCount = pasteboard.changeCount
        // Save all representations so the reader's copy command need not replace the user's existing clipboard.
        var previousItems: [NSPasteboardItem] = []
        for item in pasteboard.pasteboardItems ?? [] {
            let snapshot = NSPasteboardItem()
            for type in item.types {
                guard let data = item.data(forType: type), snapshot.setData(data, forType: type) else {
                    reply(nil, "未能保留目前的剪貼簿，未能查詢最近報讀。")
                    return
                }
            }
            previousItems.append(snapshot)
        }
        guard pasteboard.changeCount == initialCount else {
            reply(nil, "剪貼簿剛有變動，請再試一次。")
            return
        }
        for event in [down, up] {
            event.flags = [.maskControl, .maskAlternate, .maskShift]
            event.setIntegerValueField(.eventSourceUserData, value: Self.copyEventMarker)
            event.post(tap: .cghidEventTap)
        }

        let deadline = DispatchTime.now() + .milliseconds(800)
        func poll() {
            let count = pasteboard.changeCount
            if count != initialCount, let items = pasteboard.pasteboardItems, items.count == 1,
               items[0].types == [.string], let phrase = items[0].string(forType: .string),
               pasteboard.changeCount == count {
                pasteboard.clearContents()
                if !previousItems.isEmpty, !pasteboard.writeObjects(previousItems) {
                    reply(nil, "未能還原原來的剪貼簿。")
                    return
                }
                reply(phrase, "")
            } else if DispatchTime.now() < deadline {
                DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(30), execute: poll)
            } else {
                reply(nil, "未能取得最近的報讀內容。請確認 Control Option Shift C 可在 TurnOver 複製最近報讀。")
            }
        }
        poll()
    }
}
