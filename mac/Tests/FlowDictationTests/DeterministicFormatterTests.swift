import Testing
@testable import FlowDictation

@Suite("DeterministicFormatter")
struct DeterministicFormatterTests {
    private let formatter = DeterministicFormatter()

    @Test("normalizes layout, dictionary terms, capitalization, and punctuation")
    func completeNormalization() {
        let raw = "  hello   open ai new line use post grass cue el  "
        let result = formatter.format(
            raw,
            corrections: [
                DictionaryCorrection(spoken: "open ai", replacement: "OpenAI"),
                DictionaryCorrection(spoken: "post grass cue el", replacement: "PostgreSQL"),
            ]
        )

        #expect(result.rawText == raw)
        #expect(result.formattedText == "Hello OpenAI\nUse PostgreSQL.")
        #expect(result.changed)
    }

    @Test("dictionary terms match whole tokens, not substrings")
    func tokenBoundary() {
        let result = formatter.format(
            "scat cat category",
            corrections: [DictionaryCorrection(spoken: "cat", replacement: "dog")]
        )
        #expect(result.formattedText == "Scat dog category.")
    }

    @Test("the longest overlapping dictionary term wins")
    func longestMatch() {
        let result = formatter.format(
            "new york city",
            corrections: [
                DictionaryCorrection(spoken: "new york", replacement: "NY"),
                DictionaryCorrection(spoken: "new york city", replacement: "NYC"),
            ]
        )
        #expect(result.formattedText == "NYC.")
    }

    @Test("existing punctuation is preserved and empty input stays empty")
    func punctuation() {
        #expect(formatter.format("already done!", corrections: []).formattedText == "Already done!")
        #expect(formatter.format("", corrections: []).formattedText == "")
        #expect(formatter.format("   ", corrections: []).formattedText == "")
    }
}
