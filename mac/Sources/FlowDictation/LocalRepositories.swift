import Foundation
import FlowCore

public protocol DataFileClient: Sendable {
    func read(from url: URL) throws -> Data?
    func writeAtomically(_ data: Data, to url: URL) throws
}

public struct LocalDataFileClient: DataFileClient {
    public init() {}

    public func read(from url: URL) throws -> Data? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try Data(contentsOf: url)
    }

    public func writeAtomically(_ data: Data, to url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try data.write(to: url, options: .atomic)
    }
}

public enum LocalRepositoryError: Error, Sendable, Equatable {
    case corruptStorage
    case duplicateFinal(DictationSessionID)
    case duplicateDictionarySpoken(String)
    case transcriptNotFound(DictationSessionID)
    case dictionaryEntryNotFound(UUID)
}

public actor LocalTranscriptStore: TranscriptStore {
    private let fileURL: URL
    private let retentionLimit: Int
    private let files: any DataFileClient
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private var cache: [StoredTranscript]?

    public init(
        fileURL: URL,
        retentionLimit: Int = 500,
        files: any DataFileClient = LocalDataFileClient()
    ) {
        precondition(retentionLimit > 0, "Retention must keep at least one transcript")
        self.fileURL = fileURL
        self.retentionLimit = retentionLimit
        self.files = files
        (encoder, decoder) = Self.makeCoders()
    }

    public func append(_ transcript: Transcript) throws {
        var records = try load()
        guard !records.contains(where: { $0.id == transcript.id }) else {
            throw LocalRepositoryError.duplicateFinal(transcript.id)
        }

        records.append(StoredTranscript(transcript: transcript))
        records.sort { $0.transcript.createdAt < $1.transcript.createdAt }
        if records.count > retentionLimit {
            records.removeFirst(records.count - retentionLimit)
        }
        try commit(records)
    }

    public func records() throws -> [StoredTranscript] {
        try load().sorted { $0.transcript.createdAt > $1.transcript.createdAt }
    }

    public func search(_ query: String) throws -> [StoredTranscript] {
        let query = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return try records() }
        return try records().filter {
            $0.transcript.rawText.localizedCaseInsensitiveContains(query)
                || $0.transcript.formattedText.localizedCaseInsensitiveContains(query)
        }
    }

    public func delete(id: DictationSessionID) throws {
        var records = try load()
        guard let index = records.firstIndex(where: { $0.id == id }) else {
            throw LocalRepositoryError.transcriptNotFound(id)
        }
        records.remove(at: index)
        try commit(records)
    }

    public func recordInsertion(
        _ outcome: InjectionOutcome,
        for id: DictationSessionID,
        at date: Date = Date()
    ) throws {
        var records = try load()
        guard let index = records.firstIndex(where: { $0.id == id }) else {
            throw LocalRepositoryError.transcriptNotFound(id)
        }
        records[index].injectionOutcome = outcome
        records[index].updatedAt = date
        try commit(records)
    }

    public func lastSuccessfulTranscript() throws -> Transcript? {
        try load()
            .filter { !$0.transcript.formattedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .max { $0.transcript.createdAt < $1.transcript.createdAt }?
            .transcript
    }

    private func load() throws -> [StoredTranscript] {
        if let cache { return cache }
        do {
            guard let data = try files.read(from: fileURL) else {
                cache = []
                return []
            }
            let records = try decoder.decode([StoredTranscript].self, from: data)
            cache = records
            return records
        } catch {
            throw LocalRepositoryError.corruptStorage
        }
    }

    private func commit(_ records: [StoredTranscript]) throws {
        let data = try encoder.encode(records)
        try files.writeAtomically(data, to: fileURL)
        cache = records
    }

    private static func makeCoders() -> (JSONEncoder, JSONDecoder) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (encoder, decoder)
    }
}

public actor LocalDictionaryStore: DictionaryStore {
    private let fileURL: URL
    private let files: any DataFileClient
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private var cache: [DictionaryCorrection]?

    public init(
        fileURL: URL,
        files: any DataFileClient = LocalDataFileClient()
    ) {
        self.fileURL = fileURL
        self.files = files
        (encoder, decoder) = Self.makeCoders()
    }

    public func corrections() throws -> [DictionaryCorrection] {
        try load().sorted {
            if $0.spoken.count == $1.spoken.count {
                return $0.spoken.localizedStandardCompare($1.spoken) == .orderedAscending
            }
            return $0.spoken.count > $1.spoken.count
        }
    }

    public func upsert(_ correction: DictionaryCorrection) throws {
        var corrections = try load()
        let duplicate = corrections.first {
            $0.id != correction.id
                && $0.spoken.compare(correction.spoken, options: [.caseInsensitive, .diacriticInsensitive]) == .orderedSame
        }
        guard duplicate == nil else {
            throw LocalRepositoryError.duplicateDictionarySpoken(correction.spoken)
        }

        if let index = corrections.firstIndex(where: { $0.id == correction.id }) {
            corrections[index] = correction
        } else {
            corrections.append(correction)
        }
        try commit(corrections)
    }

    public func delete(id: UUID) throws {
        var corrections = try load()
        guard let index = corrections.firstIndex(where: { $0.id == id }) else {
            throw LocalRepositoryError.dictionaryEntryNotFound(id)
        }
        corrections.remove(at: index)
        try commit(corrections)
    }

    private func load() throws -> [DictionaryCorrection] {
        if let cache { return cache }
        do {
            guard let data = try files.read(from: fileURL) else {
                cache = []
                return []
            }
            let corrections = try decoder.decode([DictionaryCorrection].self, from: data)
            cache = corrections
            return corrections
        } catch {
            throw LocalRepositoryError.corruptStorage
        }
    }

    private func commit(_ corrections: [DictionaryCorrection]) throws {
        let data = try encoder.encode(corrections)
        try files.writeAtomically(data, to: fileURL)
        cache = corrections
    }

    private static func makeCoders() -> (JSONEncoder, JSONDecoder) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (encoder, decoder)
    }
}
