import Foundation
import FlowCore

public enum TargetValidation: Sendable, Equatable {
    case valid
    case noTarget
    case secureTarget
    case permissionDenied
    case targetChanged
}

public struct PasteboardRepresentation: Sendable, Equatable {
    public let type: String
    public let data: Data

    public init(type: String, data: Data) {
        self.type = type
        self.data = data
    }
}

public struct PasteboardItemSnapshot: Sendable, Equatable {
    public let representations: [PasteboardRepresentation]

    public init(representations: [PasteboardRepresentation]) {
        self.representations = representations
    }
}

public struct PasteboardSnapshot: Sendable, Equatable {
    public let items: [PasteboardItemSnapshot]

    public init(items: [PasteboardItemSnapshot]) {
        self.items = items
    }
}

public protocol FocusTargetInspecting: Sendable {
    func captureTarget() async -> TargetCapture
    func validate(_ target: FocusTarget) async -> TargetValidation
}

public protocol PasteboardAccessing: Sendable {
    func snapshot() async throws -> PasteboardSnapshot
    /// Returns the change count immediately after this app writes the transcript.
    func writeText(_ text: String) async throws -> Int
    func changeCount() async -> Int
    func restore(_ snapshot: PasteboardSnapshot) async throws
}

public protocol PasteCommandPosting: Sendable {
    func postPaste() async throws
}

public protocol PasteCompletionWaiting: Sendable {
    func waitForCompletion(timeout: Duration) async -> Bool
}

/// Target and clipboard policy isolated from AppKit so every safety path is unit-testable.
public actor SafeTextInjector: TextInjector {
    private let targets: any FocusTargetInspecting
    private let pasteboard: any PasteboardAccessing
    private let pasteCommand: any PasteCommandPosting
    private let completion: any PasteCompletionWaiting

    public init(
        targets: any FocusTargetInspecting,
        pasteboard: any PasteboardAccessing,
        pasteCommand: any PasteCommandPosting,
        completion: any PasteCompletionWaiting
    ) {
        self.targets = targets
        self.pasteboard = pasteboard
        self.pasteCommand = pasteCommand
        self.completion = completion
    }

    public func captureTarget() async -> TargetCapture {
        await targets.captureTarget()
    }

    public func inject(
        _ text: String,
        into capture: TargetCapture,
        timeout: Duration = .milliseconds(250)
    ) async -> InjectionResult {
        guard !Task.isCancelled else {
            return InjectionResult(outcome: .cancelled)
        }
        let target: FocusTarget
        switch capture {
        case .target(let captured):
            target = captured
        case .noTarget:
            return InjectionResult(outcome: .noTarget)
        case .secureTarget:
            return InjectionResult(outcome: .secureTarget)
        case .permissionDenied:
            return InjectionResult(outcome: .permissionDenied)
        }
        guard target.isEditable else {
            return InjectionResult(outcome: .noTarget)
        }
        guard !target.isSecure else {
            return InjectionResult(outcome: .secureTarget)
        }

        switch await targets.validate(target) {
        case .valid:
            break
        case .noTarget:
            return InjectionResult(outcome: .noTarget)
        case .secureTarget:
            return InjectionResult(outcome: .secureTarget)
        case .permissionDenied:
            return InjectionResult(outcome: .permissionDenied)
        case .targetChanged:
            return InjectionResult(outcome: .targetChanged)
        }

        guard !Task.isCancelled else {
            return InjectionResult(outcome: .cancelled)
        }

        do {
            let original = try await pasteboard.snapshot()
            let appChangeCount = try await pasteboard.writeText(text)

            guard !Task.isCancelled else {
                let restored = await guardedRestore(
                    original,
                    expectedChangeCount: appChangeCount
                )
                return InjectionResult(outcome: .cancelled, clipboardRestored: restored)
            }

            // Focus can move while clipboard access suspends. Revalidate at the last possible
            // point before posting Cmd-V, then restore our clipboard write if the target changed.
            let revalidation = await targets.validate(target)
            guard revalidation == .valid else {
                let restored = await guardedRestore(
                    original,
                    expectedChangeCount: appChangeCount
                )
                return InjectionResult(
                    outcome: outcome(for: revalidation),
                    clipboardRestored: restored
                )
            }

            do {
                try await pasteCommand.postPaste()
            } catch {
                let restored = await guardedRestore(
                    original,
                    expectedChangeCount: appChangeCount
                )
                return InjectionResult(outcome: .timedOut, clipboardRestored: restored)
            }

            let completed = await completion.waitForCompletion(timeout: timeout)
            let restored = await guardedRestore(
                original,
                expectedChangeCount: appChangeCount
            )
            return InjectionResult(
                outcome: completed ? .inserted : .timedOut,
                clipboardRestored: restored
            )
        } catch {
            return InjectionResult(outcome: .timedOut)
        }
    }

    private func outcome(for validation: TargetValidation) -> InjectionOutcome {
        switch validation {
        case .valid: .inserted
        case .noTarget: .noTarget
        case .secureTarget: .secureTarget
        case .permissionDenied: .permissionDenied
        case .targetChanged: .targetChanged
        }
    }

    private func guardedRestore(
        _ snapshot: PasteboardSnapshot,
        expectedChangeCount: Int
    ) async -> Bool {
        guard await pasteboard.changeCount() == expectedChangeCount else { return false }
        do {
            try await pasteboard.restore(snapshot)
            return true
        } catch {
            return false
        }
    }
}
