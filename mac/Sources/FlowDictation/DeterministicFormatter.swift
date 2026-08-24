import Foundation

public struct DictionaryCorrection: Sendable, Codable, Equatable, Identifiable {
    public let id: UUID
    public var spoken: String
    public var replacement: String
    public let createdAt: Date
    public var updatedAt: Date

    public init(
        id: UUID = UUID(),
        spoken: String,
        replacement: String,
        createdAt: Date = Date(),
        updatedAt: Date? = nil
    ) {
        self.id = id
        self.spoken = spoken
        self.replacement = replacement
        self.createdAt = createdAt
        self.updatedAt = updatedAt ?? createdAt
    }
}

public struct DeterministicFormatter: Formatter {
    public init() {}

    public func format(
        _ rawText: String,
        corrections: [DictionaryCorrection] = []
    ) -> FormattingResult {
        var text = rawText.replacingOccurrences(of: "\r\n", with: "\n")
        text = replaceSpokenLayoutMarkers(in: text)
        text = normalizeWhitespace(in: text)
        text = applyCorrections(corrections, to: text)
        text = capitalizeParagraphs(in: text)
        text = addTerminalPunctuation(to: text)
        return FormattingResult(rawText: rawText, formattedText: text)
    }

    private func replaceSpokenLayoutMarkers(in text: String) -> String {
        var result = text
        result = replacingPattern(
            #"(?i)(?<![\p{L}\p{N}_])(?:new\s+paragraph|start\s+a\s+new\s+paragraph)(?![\p{L}\p{N}_])"#,
            with: "\n\n",
            in: result
        )
        result = replacingPattern(
            #"(?i)(?<![\p{L}\p{N}_])(?:new\s+line|next\s+line|line\s+break)(?![\p{L}\p{N}_])"#,
            with: "\n",
            in: result
        )
        return result
    }

    private func normalizeWhitespace(in text: String) -> String {
        let lines = text.components(separatedBy: "\n").map { line in
            replacingPattern(#"[\t ]+"#, with: " ", in: line)
                .trimmingCharacters(in: .whitespaces)
        }

        var output: [String] = []
        var previousWasBlank = false
        for line in lines {
            let isBlank = line.isEmpty
            if isBlank && previousWasBlank { continue }
            output.append(line)
            previousWasBlank = isBlank
        }
        return output.joined(separator: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func applyCorrections(
        _ corrections: [DictionaryCorrection],
        to text: String
    ) -> String {
        corrections
            .filter { !$0.spoken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .sorted {
                if $0.spoken.count == $1.spoken.count {
                    return $0.spoken.localizedStandardCompare($1.spoken) == .orderedAscending
                }
                return $0.spoken.count > $1.spoken.count
            }
            .reduce(text) { partial, correction in
                let escaped = NSRegularExpression.escapedPattern(for: correction.spoken)
                let pattern = "(?i)(?<![\\p{L}\\p{N}_])\(escaped)(?![\\p{L}\\p{N}_])"
                return replacingPattern(pattern, with: correction.replacement, in: partial)
            }
    }

    private func capitalizeParagraphs(in text: String) -> String {
        text.components(separatedBy: "\n").map { line in
            guard let firstLetter = line.rangeOfCharacter(from: .letters) else { return line }
            return line.replacingCharacters(
                in: firstLetter,
                with: String(line[firstLetter]).uppercased()
            )
        }.joined(separator: "\n")
    }

    private func addTerminalPunctuation(to text: String) -> String {
        guard let last = text.last, last.isLetter || last.isNumber else { return text }
        return text + "."
    }

    private func replacingPattern(
        _ pattern: String,
        with replacement: String,
        in text: String
    ) -> String {
        guard let expression = try? NSRegularExpression(pattern: pattern) else { return text }
        return expression.stringByReplacingMatches(
            in: text,
            range: NSRange(text.startIndex..<text.endIndex, in: text),
            withTemplate: NSRegularExpression.escapedTemplate(for: replacement)
        )
    }
}
