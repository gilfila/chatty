import Foundation
import Testing
import FlowCore
@testable import FlowDictation

private final class MemoryDataFileClient: DataFileClient, @unchecked Sendable {
    private let lock = NSLock()
    private var values: [URL: Data] = [:]
    private var writeFailure: (any Error)?

    func read(from url: URL) throws -> Data? {
        lock.withLock { values[url] }
    }

    func writeAtomically(_ data: Data, to url: URL) throws {
        try lock.withLock {
            if let writeFailure { throw writeFailure }
            values[url] = data
        }
    }

    func seed(_ data: Data, at url: URL) {
        lock.withLock { values[url] = data }
    }

    func failWrites(with error: any Error) {
        lock.withLock { writeFailure = error }
    }

    func data(at url: URL) -> Data? {
        lock.withLock { values[url] }
    }
}

private enum TestFileError: Error { case writeFailed }

@Suite("LocalTranscriptStore")
struct LocalTranscriptStoreTests {
    private func transcript(
        id: DictationSessionID = DictationSessionID(),
        createdAt: Date,
        raw: String,
        formatted: String
    ) -> Transcript {
        Transcript(
            id: id,
            createdAt: createdAt,
            rawText: raw,
            formattedText: formatted,
            localeIdentifier: "en-US",
            audioDuration: 1
        )
    }

    @Test("append, reload, search, outcome update, and delete are durable")
    func lifecycle() async throws {
        let files = MemoryDataFileClient()
        let url = URL(fileURLWithPath: "/virtual/history.json")
        let old = transcript(
            createdAt: Date(timeIntervalSince1970: 1),
            raw: "open eye",
            formatted: "OpenAI."
        )
        let recent = transcript(
            createdAt: Date(timeIntervalSince1970: 2),
            raw: "second",
            formatted: "Second."
        )

        let store = LocalTranscriptStore(fileURL: url, files: files)
        try await store.append(old)
        try await store.append(recent)

        let reloaded = LocalTranscriptStore(fileURL: url, files: files)
        #expect(try await reloaded.records().map(\.id) == [recent.id, old.id])
        #expect(try await reloaded.search("OPENAI").map(\.id) == [old.id])

        try await reloaded.recordInsertion(
            .targetChanged,
            for: recent.id,
            at: Date(timeIntervalSince1970: 3)
        )
        let records = try await reloaded.records()
        #expect(records.first?.injectionOutcome == .targetChanged)
        #expect(try await reloaded.lastSuccessfulTranscript()?.id == recent.id)

        try await reloaded.delete(id: old.id)
        #expect(try await reloaded.records().map(\.id) == [recent.id])
    }

    @Test("a session final cannot be appended twice")
    func duplicateFinal() async throws {
        let files = MemoryDataFileClient()
        let store = LocalTranscriptStore(
            fileURL: URL(fileURLWithPath: "/virtual/history.json"),
            files: files
        )
        let id = DictationSessionID()
        try await store.append(
            transcript(id: id, createdAt: .now, raw: "one", formatted: "One.")
        )

        await #expect(throws: LocalRepositoryError.duplicateFinal(id)) {
            try await store.append(
                transcript(id: id, createdAt: .now, raw: "two", formatted: "Two.")
            )
        }
    }

    @Test("retention removes only the oldest excess records")
    func retention() async throws {
        let store = LocalTranscriptStore(
            fileURL: URL(fileURLWithPath: "/virtual/history.json"),
            retentionLimit: 2,
            files: MemoryDataFileClient()
        )
        for second in 1...3 {
            try await store.append(
                transcript(
                    createdAt: Date(timeIntervalSince1970: TimeInterval(second)),
                    raw: "\(second)",
                    formatted: "\(second)."
                )
            )
        }
        #expect(try await store.records().map(\.transcript.rawText) == ["3", "2"])
    }

    @Test("corrupt storage is surfaced without being overwritten")
    func corruptStorage() async throws {
        let files = MemoryDataFileClient()
        let url = URL(fileURLWithPath: "/virtual/history.json")
        let invalid = Data("not json".utf8)
        files.seed(invalid, at: url)
        let store = LocalTranscriptStore(fileURL: url, files: files)

        await #expect(throws: LocalRepositoryError.corruptStorage) {
            _ = try await store.records()
        }
        #expect(files.data(at: url) == invalid)
    }

    @Test("a failed atomic write leaves the last good in-memory and persisted state intact")
    func failedWrite() async throws {
        let files = MemoryDataFileClient()
        let url = URL(fileURLWithPath: "/virtual/history.json")
        let store = LocalTranscriptStore(fileURL: url, files: files)
        let first = transcript(createdAt: .now, raw: "one", formatted: "One.")
        try await store.append(first)
        let lastGoodData = files.data(at: url)
        files.failWrites(with: TestFileError.writeFailed)

        await #expect(throws: (any Error).self) {
            try await store.append(
                self.transcript(createdAt: .now, raw: "two", formatted: "Two.")
            )
        }
        #expect(try await store.records().map(\.id) == [first.id])
        #expect(files.data(at: url) == lastGoodData)
    }
}

@Suite("LocalDictionaryStore")
struct LocalDictionaryStoreTests {
    @Test("dictionary entries are editable, durable, and longest-first")
    func lifecycle() async throws {
        let files = MemoryDataFileClient()
        let url = URL(fileURLWithPath: "/virtual/dictionary.json")
        let store = LocalDictionaryStore(fileURL: url, files: files)
        let short = DictionaryCorrection(spoken: "new york", replacement: "NY")
        var long = DictionaryCorrection(spoken: "new york city", replacement: "NYC")
        try await store.upsert(short)
        try await store.upsert(long)
        #expect(try await store.corrections().map(\.id) == [long.id, short.id])

        long.replacement = "New York City"
        long.updatedAt = Date(timeIntervalSince1970: 5)
        try await store.upsert(long)

        let reloaded = LocalDictionaryStore(fileURL: url, files: files)
        #expect(try await reloaded.corrections().first?.replacement == "New York City")
        try await reloaded.delete(id: short.id)
        #expect(try await reloaded.corrections().map(\.id) == [long.id])
    }

    @Test("a spoken phrase cannot map to two dictionary entries")
    func duplicateSpokenPhrase() async throws {
        let store = LocalDictionaryStore(
            fileURL: URL(fileURLWithPath: "/virtual/dictionary.json"),
            files: MemoryDataFileClient()
        )
        try await store.upsert(
            DictionaryCorrection(spoken: "résumé", replacement: "CV")
        )

        await #expect(throws: LocalRepositoryError.duplicateDictionarySpoken("RESUME")) {
            try await store.upsert(
                DictionaryCorrection(spoken: "RESUME", replacement: "summary")
            )
        }
    }
}
