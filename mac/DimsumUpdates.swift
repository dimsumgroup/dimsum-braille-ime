import Foundation
import CryptoKit
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

struct DimsumVersion: Comparable {
    private let components: [Int]

    init?(_ text: String) {
        let tag = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let number = tag.lowercased().hasPrefix("v") ? String(tag.dropFirst()) : tag
        let parts = number.split(separator: ".", omittingEmptySubsequences: false)
        guard (2...3).contains(parts.count),
              parts.allSatisfy({ !$0.isEmpty && $0.utf8.allSatisfy { (48...57).contains($0) } }),
              parts.compactMap({ Int($0) }).count == parts.count else { return nil }
        components = parts.compactMap { Int($0) } + Array(repeating: 0, count: 3 - parts.count)
    }

    static func < (lhs: Self, rhs: Self) -> Bool {
        lhs.components.lexicographicallyPrecedes(rhs.components)
    }
}

struct DimsumDictionaryHeader {
    let version: String?
    let date: String?

    init(data: Data) {
        let text = String(decoding: data.prefix(4096), as: UTF8.self)
        let lines = text.replacingOccurrences(of: "\u{FEFF}", with: "")
            .split(whereSeparator: \.isNewline)
        var date: String?
        var version: String?
        for line in lines {
            if line == "..." { break }
            if line.hasPrefix("#"), date == nil,
               let match = line.range(of: #"^#\s*[0-9]{4}-[0-9]{2}-[0-9]{2}\b"#, options: .regularExpression) {
                date = String(line[match].suffix(10))
            }
            if line.hasPrefix("version:") {
                version = line.dropFirst("version:".count)
                    .trimmingCharacters(in: CharacterSet.whitespaces.union(CharacterSet(charactersIn: "\"'")))
            }
        }
        self.version = version
        self.date = date
    }

    var summary: String {
        [version.map { "版本 \($0)" }, date].compactMap { $0 }.joined(separator: "・")
    }

    func isOlder(than other: Self) -> Bool {
        if let local = version.flatMap(DimsumVersion.init),
           let remote = other.version.flatMap(DimsumVersion.init), local != remote {
            return local < remote
        }
        if let localDate = date, let remoteDate = other.date, localDate != remoteDate {
            return localDate < remoteDate
        }
        return false
    }
}

private enum GitBlob {
    static func sha(of data: Data) -> String {
        var digest = Insecure.SHA1()
        digest.update(data: Data("blob \(data.count)\u{0}".utf8))
        digest.update(data: data)
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }

    static func isSHA(_ value: String) -> Bool {
        value.utf8.count == 40 && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
}

struct DimsumInstalledDictionary {
    static let filename = "dimsum_cb.dict.yaml"
    let gitHash: String
    let header: DimsumDictionaryHeader

    init?(at url: URL) {
        guard let data = try? Data(contentsOf: url, options: .mappedIfSafe), !data.isEmpty else { return nil }
        gitHash = GitBlob.sha(of: data)
        header = DimsumDictionaryHeader(data: data)
    }

    var summary: String {
        let hash = String(gitHash.prefix(12))
        return header.summary.isEmpty ? hash : "\(header.summary)（\(hash)）"
    }
}

struct DimsumRelease: Decodable, Equatable {
    struct Asset: Decodable, Equatable {
        let name: String

        var isMacInstaller: Bool {
            let filename = name.lowercased()
            let tokens = Set(filename.split(whereSeparator: { !$0.isLetter && !$0.isNumber }))
            guard tokens.isDisjoint(with: ["source", "sources", "src", "squirrel", "debug", "dsym", "symbols",
                                           "windows", "win32", "win64", "win", "linux"]),
                  !filename.contains("squirrel") else { return false }
            if filename.hasSuffix(".dmg") || filename.hasSuffix(".pkg") { return true }
            return filename.hasSuffix(".zip")
                && !tokens.isDisjoint(with: ["macos", "mac", "darwin", "osx"])
        }
    }

    let tag: String
    let page: URL
    let draft: Bool
    let prerelease: Bool
    let assets: [Asset]

    enum CodingKeys: String, CodingKey {
        case tag = "tag_name"
        case page = "html_url"
        case draft, prerelease, assets
    }

    static func latestMacRelease(in releases: [Self], newerThan installed: DimsumVersion) -> Self? {
        releases.compactMap { release -> (Self, DimsumVersion)? in
            guard !release.draft, !release.prerelease,
                  release.page.scheme == "https", release.page.host == "github.com",
                  release.page.path.hasPrefix("/dimsumgroup/dimsum-braille-ime/releases/tag/"),
                  release.assets.contains(where: \.isMacInstaller),
                  let version = DimsumVersion(release.tag), version > installed else { return nil }
            return (release, version)
        }.max { $0.1 < $1.1 }?.0
    }
}

struct DimsumDictionaryUpdate: Equatable {
    let gitHash: String
    let summary: String
}

struct DimsumPronunciationSources {
    static let filenames = ["characterDescriptions.dic", "symbols.dic", "cldr.dic"]
    static let tableFilename = "DimsumPronunciations.json"
    static let manifestFilename = "nvda-source-hashes.json"

    let bundledResources: URL
    let userOutput: URL

    private struct Manifest: Decodable {
        let tableSHA: String
        let sourceSHAs: [String: String]
    }

    func appliedHashes() -> [String: String]? {
        let userTableURL = userOutput.appendingPathComponent(Self.tableFilename)
        let userTable = try? Data(contentsOf: userTableURL, options: .mappedIfSafe)
        if let userTable, let hashes = recordedHashes(for: userTable, in: userOutput) {
            return hashes
        }

        let bundledOutput = bundledResources.appendingPathComponent("voiceover_output", isDirectory: true)
        guard let bundledTable = try? Data(contentsOf: bundledOutput.appendingPathComponent(Self.tableFilename),
                                           options: .mappedIfSafe),
              let hashes = recordedHashes(for: bundledTable, in: bundledOutput) else { return nil }
        if let userTable, userTable != bundledTable,
           let userEntries = try? JSONDecoder().decode([String: String].self, from: userTable) {
            // 舊版沒有紀錄時，只有發音表等同內附版本才可沿用其來源雜湊。
            guard let bundledEntries = try? JSONDecoder().decode([String: String].self, from: bundledTable),
                  userEntries == bundledEntries else { return nil }
        }
        return hashes
    }

    private func recordedHashes(for table: Data, in directory: URL) -> [String: String]? {
        guard let manifestData = try? Data(contentsOf: directory.appendingPathComponent(Self.manifestFilename)),
              let manifest = try? JSONDecoder().decode(Manifest.self, from: manifestData),
              manifest.tableSHA == GitBlob.sha(of: table),
              Set(manifest.sourceSHAs.keys) == Set(Self.filenames),
              manifest.sourceSHAs.values.allSatisfy(GitBlob.isSHA) else { return nil }
        return manifest.sourceSHAs
    }
}

struct DimsumPronunciationUpdate: Equatable {
    let sourceSHAs: [String: String]
    let changedFiles: [String]
}

enum DimsumPronunciationStatus {
    case current
    case unrecorded
    case update(DimsumPronunciationUpdate)
}

enum DimsumUpdates {
    private static let repository = "https://api.github.com/repos/dimsumgroup/dimsum-braille-ime"
    private static let dictionaryPath = "Dimsum%20dic%20and%20schema/dimsum_cb.dict.yaml"

    private struct DictionaryMetadata: Decodable {
        let sha: String
        let downloadURL: URL

        enum CodingKeys: String, CodingKey {
            case sha
            case downloadURL = "download_url"
        }
    }

    static func check(appVersion: String, dictionaryURL: URL, session: URLSession = .shared) async
        -> (Result<DimsumRelease?, Error>, Result<DimsumDictionaryUpdate?, Error>) {
        async let programResult = result {
            guard let installed = DimsumVersion(appVersion) else { throw URLError(.cannotParseResponse) }
            return try await program(after: installed, session: session)
        }
        async let dictionaryResult = result { try await dictionary(at: dictionaryURL, session: session) }
        return await (programResult, dictionaryResult)
    }

    private static func result<Value>(_ operation: () async throws -> Value) async -> Result<Value, Error> {
        do { return .success(try await operation()) }
        catch { return .failure(error) }
    }

    static func program(after installed: DimsumVersion, session: URLSession = .shared) async throws -> DimsumRelease? {
        var latest: DimsumRelease?
        var newest = installed
        var page = 1
        while true {
            let endpoint = URL(string: "\(repository)/releases?per_page=100&page=\(page)")!
            let data = try await fetch(URLRequest(url: endpoint), session: session)
            let releases = try JSONDecoder().decode([DimsumRelease].self, from: data)
            if let release = DimsumRelease.latestMacRelease(in: releases, newerThan: newest),
               let version = DimsumVersion(release.tag) {
                latest = release
                newest = version
            }
            if releases.count < 100 { return latest }
            page += 1
        }
    }

    static func dictionary(at installedURL: URL, session: URLSession = .shared) async throws -> DimsumDictionaryUpdate? {
        guard FileManager.default.fileExists(atPath: installedURL.path) else { return nil }
        guard let local = DimsumInstalledDictionary(at: installedURL) else { throw CocoaError(.fileReadUnknown) }
        let endpoint = URL(string: "\(repository)/contents/\(dictionaryPath)?ref=main")!
        let data = try await fetch(URLRequest(url: endpoint), session: session)
        let remote = try JSONDecoder().decode(DictionaryMetadata.self, from: data)
        guard remote.sha != local.gitHash else { return nil }
        guard remote.downloadURL.scheme == "https", remote.downloadURL.host == "raw.githubusercontent.com",
              remote.downloadURL.path.hasPrefix("/dimsumgroup/dimsum-braille-ime/") else { throw URLError(.badURL) }
        var headerRequest = URLRequest(url: remote.downloadURL)
        headerRequest.setValue("bytes=0-4095", forHTTPHeaderField: "Range")
        let header = try await fetch(headerRequest, session: session)
        let remoteHeader = DimsumDictionaryHeader(data: header)
        guard remoteHeader.version != nil else { throw URLError(.cannotParseResponse) }
        guard !remoteHeader.isOlder(than: local.header) else { return nil }
        let hash = String(remote.sha.prefix(12))
        let summary = remoteHeader.summary.isEmpty ? hash : "\(remoteHeader.summary)（\(hash)）"
        return DimsumDictionaryUpdate(gitHash: remote.sha, summary: summary)
    }

    static func checkPronunciations(_ sources: DimsumPronunciationSources, session: URLSession = .shared) async
        -> Result<DimsumPronunciationStatus, Error> {
        await result { try await pronunciations(for: sources, session: session) }
    }

    private struct SourceMetadata: Decodable {
        let name: String
        let sha: String
        let type: String
    }

    private static func pronunciations(for sources: DimsumPronunciationSources, session: URLSession) async throws
        -> DimsumPronunciationStatus {
        guard let local = sources.appliedHashes() else { return .unrecorded }
        let endpoint = URL(string: "\(repository)/contents/NVDA%20dic?ref=main")!
        let data = try await fetch(URLRequest(url: endpoint), session: session)
        let entries = try JSONDecoder().decode([SourceMetadata].self, from: data)
        var remote: [String: String] = [:]
        for filename in DimsumPronunciationSources.filenames {
            guard let entry = entries.first(where: { $0.name == filename && $0.type == "file" }),
                  GitBlob.isSHA(entry.sha) else { throw URLError(.cannotParseResponse) }
            remote[filename] = entry.sha
        }
        let changed = DimsumPronunciationSources.filenames.filter { local[$0] != remote[$0] }
        if changed.isEmpty { return .current }
        return .update(DimsumPronunciationUpdate(sourceSHAs: remote, changedFiles: changed))
    }

    private static func fetch(_ original: URLRequest, session: URLSession) async throws -> Data {
        var request = original
        request.timeoutInterval = 12
        request.setValue("DimsumManager", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse, (200..<300).contains(httpResponse.statusCode) else {
            throw URLError(.badServerResponse)
        }
        return data
    }
}
