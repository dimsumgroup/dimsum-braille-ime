import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

final class UpdateFixtureProtocol: URLProtocol {
    static var respond: ((URLRequest) throws -> (Int, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}

    override func startLoading() {
        do {
            guard let respond = Self.respond, let url = request.url else { throw URLError(.badURL) }
            let (code, data) = try respond(request)
            let response = HTTPURLResponse(url: url, statusCode: code, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }
}

@main
struct UpdateChecks {
    static func main() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [UpdateFixtureProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }

        func release(_ tag: String, _ assets: [String], draft: Bool = false, prerelease: Bool = false) -> [String: Any] {
            [
                "tag_name": tag,
                "html_url": "https://github.com/dimsumgroup/dimsum-braille-ime/releases/tag/\(tag)",
                "draft": draft,
                "prerelease": prerelease,
                "assets": assets.map { ["name": $0] },
            ]
        }

        let installed = DimsumVersion("V1.1.1")!
        let ignored = [
            release("v1.2.0", ["Dimsum_Setup.exe"]),
            release("v1.2.1", []),
            release("v1.2.2", ["Dimsum-macOS-source.zip"]),
            release("v1.2.3", ["Squirrel-1.1.2.pkg"]),
            release("v1.2.4", ["DimsumManager.dmg"], draft: true),
            release("v1.2.5", ["DimsumManager-macOS.zip"], prerelease: true),
            release("v1.2.6-rc.1", ["DimsumManager.dmg"]),
            release("v1.1.1", ["DimsumManager.dmg"]),
        ]
        var releases = ignored
        UpdateFixtureProtocol.respond = { _ in (200, try JSONSerialization.data(withJSONObject: releases)) }
        let noRelease = try await DimsumUpdates.program(after: installed, session: session)
        precondition(noRelease == nil)

        releases += [
            release("v1.1.2", ["DimsumManager-macOS.dmg"]),
            release("v1.1.10", ["DimsumManager-macOS.zip"]),
        ]
        let newest = try await DimsumUpdates.program(after: installed, session: session)
        precondition(newest?.tag == "v1.1.10")

        let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temp) }
        let file = temp.appendingPathComponent(DimsumInstalledDictionary.filename)
        let missing = try await DimsumUpdates.dictionary(at: file, session: session)
        precondition(missing == nil)

        func dictionary(_ date: String) -> Data {
            Data("#\(date) 新增字\n\n---\nname: dimsum_cb\nversion: \"7.0\"\n...\n字\ta\n".utf8)
        }
        try dictionary("2026-10-06").write(to: file)
        let local = DimsumInstalledDictionary(at: file)!
        precondition(local.header.date == "2026-10-06")
        precondition(local.header.version == "7.0")

        func setDictionaryResponse(hash: String, date: String) {
            UpdateFixtureProtocol.respond = { request in
                if request.url?.host == "raw.githubusercontent.com" {
                    precondition(request.value(forHTTPHeaderField: "Range") == "bytes=0-4095")
                    return (206, dictionary(date))
                }
                return (200, try JSONSerialization.data(withJSONObject: [
                    "sha": hash,
                    "download_url": "https://raw.githubusercontent.com/dimsumgroup/dimsum-braille-ime/main/dimsum_cb.dict.yaml",
                ]))
            }
        }
        setDictionaryResponse(hash: local.gitHash, date: "2026-11-21")
        let identical = try await DimsumUpdates.dictionary(at: file, session: session)
        precondition(identical == nil)
        setDictionaryResponse(hash: "0123456789012345678901234567890123456789", date: "2026-09-21")
        let older = try await DimsumUpdates.dictionary(at: file, session: session)
        precondition(older == nil)
        setDictionaryResponse(hash: "abcdef123456abcdef123456abcdef123456abcd", date: "2026-11-21")
        let update = try await DimsumUpdates.dictionary(at: file, session: session)
        precondition(update?.summary.contains("2026-11-21") == true)

        UpdateFixtureProtocol.respond = { _ in (403, Data()) }
        do {
            _ = try await DimsumUpdates.program(after: installed, session: session)
            throw CocoaError(.fileReadCorruptFile)
        } catch let error as URLError {
            precondition(error.code == .badServerResponse)
        }

        setDictionaryResponse(hash: "abcdef123456abcdef123456abcdef123456abcd", date: "2026-11-21")
        let dictionaryResponse = UpdateFixtureProtocol.respond
        UpdateFixtureProtocol.respond = { request in
            if request.url?.path.hasSuffix("/releases") == true { return (403, Data()) }
            return try dictionaryResponse!(request)
        }
        let (programResult, dictionaryResult) = await DimsumUpdates.check(
            appVersion: "1.1.1", dictionaryURL: file, session: session
        )
        if case .success = programResult { preconditionFailure("Expected an API error for releases") }
        let independentUpdate = try dictionaryResult.get()
        precondition(independentUpdate?.summary.contains("2026-11-21") == true)

        let resources = temp.appendingPathComponent("Resources", isDirectory: true)
        let bundledOutput = resources.appendingPathComponent("voiceover_output", isDirectory: true)
        let userOutput = temp.appendingPathComponent("user-output", isDirectory: true)
        for directory in [bundledOutput, userOutput] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        let sources = DimsumPronunciationSources(bundledResources: resources, userOutput: userOutput)
        let localTable = Data(#"{"日":"日子"}"#.utf8)
        let remoteTable = Data(#"{"日":"日本"}"#.utf8)
        let names = DimsumPronunciationSources.filenames
        let descriptions = names[0]
        let symbols = names[1]
        let cldr = names[2]
        let localSources = [descriptions: String(repeating: "a", count: 40),
                            symbols: String(repeating: "b", count: 40),
                            cldr: String(repeating: "c", count: 40)]
        func installTable(_ data: Data, hashes: [String: String], in directory: URL) throws {
            let output = directory.appendingPathComponent(DimsumPronunciationSources.tableFilename)
            try data.write(to: output)
            let tableSHA = DimsumInstalledDictionary(at: output)!.gitHash
            let record = ["sourceSHAs": hashes, "tableSHA": tableSHA] as [String: Any]
            try JSONSerialization.data(withJSONObject: record)
                .write(to: directory.appendingPathComponent(DimsumPronunciationSources.manifestFilename))
        }
        func setRemoteSources(_ hashes: [String: String]) {
            UpdateFixtureProtocol.respond = { request in
                precondition(request.url?.path.hasSuffix("/contents/NVDA dic") == true)
                let files = hashes.map { ["name": $0.key, "sha": $0.value, "type": "file"] }
                return (200, try JSONSerialization.data(withJSONObject: files))
            }
        }
        try installTable(localTable, hashes: localSources, in: bundledOutput)
        setRemoteSources(localSources)
        let bundledCurrent = try await DimsumUpdates.checkPronunciations(sources, session: session).get()
        if case .current = bundledCurrent {} else { preconditionFailure("Expected bundled table to match") }

        let otherChanges = localSources.merging([descriptions: String(repeating: "e", count: 40),
                                                cldr: String(repeating: "f", count: 40)]) { _, new in new }
        setRemoteSources(otherChanges)
        let descriptionsAndCldr = try await DimsumUpdates.checkPronunciations(sources, session: session).get()
        if case .update(let update) = descriptionsAndCldr {
            precondition(update.changedFiles == [descriptions, cldr])
        } else { preconditionFailure("Expected description and CLDR changes") }

        let newerSources = localSources.merging([symbols: String(repeating: "d", count: 40)]) { _, new in new }
        setRemoteSources(newerSources)
        // A cache updated by the Rime dictionary action cannot serve as the applied baseline.
        let cache = temp.appendingPathComponent("nvda_dic", isDirectory: true)
        try FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
        try Data("Downloaded NVDA word list".utf8).write(to: cache.appendingPathComponent(symbols))
        let changed = try await DimsumUpdates.checkPronunciations(sources, session: session).get()
        if case .update(let update) = changed {
            precondition(update.changedFiles == [symbols] && update.sourceSHAs == newerSources)
        } else { preconditionFailure("Expected exactly one changed NVDA input") }

        try installTable(remoteTable, hashes: newerSources, in: userOutput)
        let afterConversion = try await DimsumUpdates.checkPronunciations(sources, session: session).get()
        if case .current = afterConversion {} else { preconditionFailure("Expected successful conversion to clear notice") }

        let output = userOutput.appendingPathComponent(DimsumPronunciationSources.tableFilename)
        try Data(#"{"日":"日光"}"#.utf8).write(to: output)
        let unrecorded = try await DimsumUpdates.checkPronunciations(sources, session: session).get()
        if case .unrecorded = unrecorded {} else { preconditionFailure("Expected unknown provenance for replaced table") }
        try Data(#"{ "日": "日子" }"#.utf8).write(to: output)
        precondition(sources.appliedHashes() == localSources)
        try Data("invalid JSON".utf8).write(to: output)
        precondition(sources.appliedHashes() == localSources)

        setRemoteSources([descriptions: localSources[descriptions]!])
        let incomplete = await DimsumUpdates.checkPronunciations(sources, session: session)
        if case .success = incomplete { preconditionFailure("Incomplete API list must fail the check") }

        print("PASS: Mac releases, installed Rime dictionary, converted NVDA sources, and API errors")
    }
}
