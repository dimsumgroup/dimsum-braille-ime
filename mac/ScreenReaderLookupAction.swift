import Foundation
#if DIMSUM_LOOKUP_ACTION
import AppKit
#endif

enum ScreenReaderLookup: String, CaseIterable {
    case voiceOver = "VoiceOver"
    case turnOver = "TurnOver"

    static let request = Notification.Name("com.dimsum.braille.lookup-spoken-phrase")
    static let helperID = "com.dimsum.braille.vo-helper"
    var bundleName: String { "點心查配詞（\(rawValue)）.app" }
    var bundleID: String { "com.dimsum.braille.lookup-\(rawValue.lowercased())" }

    init?(bundleID: String) {
        guard let reader = Self.allCases.first(where: { $0.bundleID == bundleID }) else { return nil }
        self = reader
    }
}

#if DIMSUM_LOOKUP_ACTION
@main
enum LookupAction {
    static func main() {
        guard let id = Bundle.main.bundleIdentifier, let reader = ScreenReaderLookup(bundleID: id) else { return }
        guard !NSRunningApplication.runningApplications(withBundleIdentifier: ScreenReaderLookup.helperID).isEmpty else {
            let say = Process()
            say.executableURL = URL(fileURLWithPath: "/usr/bin/say")
            say.arguments = ["請先啟動點心語音報讀助手。"]
            if (try? say.run()) != nil { say.waitUntilExit() }
            return
        }
        DistributedNotificationCenter.default().postNotificationName(
            ScreenReaderLookup.request, object: reader.rawValue, userInfo: nil, deliverImmediately: true
        )
    }
}
#endif
