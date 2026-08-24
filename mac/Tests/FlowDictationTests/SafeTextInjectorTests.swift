import Foundation
import Testing
import FlowCore
@testable import FlowDictation

private actor FakeTargetInspector: FocusTargetInspecting {
    let captured: TargetCapture
    let validation: TargetValidation

    init(captured: FocusTarget?, validation: TargetValidation) {
        self.captured = captured.map(TargetCapture.target) ?? .noTarget
        self.validation = validation
    }

    func captureTarget() -> TargetCapture { captured }
    func validate(_ target: FocusTarget) -> TargetValidation { validation }
}

private actor FakePasteboard: PasteboardAccessing {
    private var currentChangeCount = 1
    private var snapshotCalls = 0
    private var restoreCalls = 0
    private var writtenText: String?
    private var mutateAfterWrite = false
    private let original = PasteboardSnapshot(items: [
        PasteboardItemSnapshot(representations: [
            PasteboardRepresentation(type: "public.utf8-plain-text", data: Data("old".utf8)),
        ]),
    ])

    func snapshot() -> PasteboardSnapshot {
        snapshotCalls += 1
        return original
    }

    func writeText(_ text: String) -> Int {
        writtenText = text
        currentChangeCount += 1
        let appCount = currentChangeCount
        if mutateAfterWrite { currentChangeCount += 1 }
        return appCount
    }

    func changeCount() -> Int { currentChangeCount }

    func restore(_ snapshot: PasteboardSnapshot) {
        restoreCalls += 1
        currentChangeCount += 1
    }

    func setMutateAfterWrite(_ value: Bool) {
        mutateAfterWrite = value
    }

    func metrics() -> (snapshotCalls: Int, restoreCalls: Int, writtenText: String?) {
        (snapshotCalls, restoreCalls, writtenText)
    }
}

private struct FakePasteCommand: PasteCommandPosting {
    let fails: Bool
    func postPaste() throws {
        if fails { throw FakeInjectionError.failed }
    }
}

private struct FakeCompletionWaiter: PasteCompletionWaiting {
    let completed: Bool
    func waitForCompletion(timeout: Duration) -> Bool { completed }
}

private enum FakeInjectionError: Error { case failed }

@Suite("SafeTextInjector")
struct SafeTextInjectorTests {
    private var validTarget: FocusTarget {
        FocusTarget(
            processID: 42,
            bundleID: "com.example.editor",
            elementSignature: "field",
            isSecure: false,
            isEditable: true
        )
    }

    private func injector(
        target: FocusTarget?,
        validation: TargetValidation,
        pasteboard: FakePasteboard = FakePasteboard(),
        pasteFails: Bool = false,
        completes: Bool = true
    ) -> SafeTextInjector {
        SafeTextInjector(
            targets: FakeTargetInspector(captured: target, validation: validation),
            pasteboard: pasteboard,
            pasteCommand: FakePasteCommand(fails: pasteFails),
            completion: FakeCompletionWaiter(completed: completes)
        )
    }

    @Test("a valid target inserts and restores an unchanged clipboard")
    func success() async {
        let pasteboard = FakePasteboard()
        let injector = injector(
            target: validTarget,
            validation: .valid,
            pasteboard: pasteboard
        )

        let result = await injector.inject("Hello", into: .target(validTarget), timeout: .seconds(1))
        let metrics = await pasteboard.metrics()
        #expect(result == InjectionResult(outcome: .inserted, clipboardRestored: true))
        #expect(metrics.snapshotCalls == 1)
        #expect(metrics.restoreCalls == 1)
        #expect(metrics.writtenText == "Hello")
    }

    @Test("a user clipboard change after transcript placement is never overwritten")
    func clipboardRace() async {
        let pasteboard = FakePasteboard()
        await pasteboard.setMutateAfterWrite(true)
        let injector = injector(
            target: validTarget,
            validation: .valid,
            pasteboard: pasteboard
        )

        let result = await injector.inject("Hello", into: .target(validTarget), timeout: .seconds(1))
        let metrics = await pasteboard.metrics()
        #expect(result == InjectionResult(outcome: .inserted, clipboardRestored: false))
        #expect(metrics.restoreCalls == 0)
    }

    @Test("all target validation failures avoid the clipboard")
    func targetFailures() async {
        let cases: [(FocusTarget?, TargetValidation, InjectionOutcome)] = [
            (nil, .noTarget, .noTarget),
            (validTarget, .noTarget, .noTarget),
            (validTarget, .secureTarget, .secureTarget),
            (validTarget, .permissionDenied, .permissionDenied),
            (validTarget, .targetChanged, .targetChanged),
        ]

        for (target, validation, expected) in cases {
            let pasteboard = FakePasteboard()
            let injector = injector(
                target: target,
                validation: validation,
                pasteboard: pasteboard
            )
            let capture = target.map(TargetCapture.target) ?? .noTarget
            let result = await injector.inject("Hello", into: capture, timeout: .seconds(1))
            let metrics = await pasteboard.metrics()
            #expect(result.outcome == expected)
            #expect(metrics.snapshotCalls == 0)
            #expect(metrics.restoreCalls == 0)
            #expect(metrics.writtenText == nil)
        }
    }

    @Test("secure and non-editable snapshots are rejected before validation")
    func snapshotRejection() async {
        for target in [
            FocusTarget(
                processID: 42,
                bundleID: nil,
                elementSignature: "password",
                isSecure: true,
                isEditable: true
            ),
            FocusTarget(
                processID: 42,
                bundleID: nil,
                elementSignature: "button",
                isSecure: false,
                isEditable: false
            ),
        ] {
            let pasteboard = FakePasteboard()
            let injector = injector(
                target: target,
                validation: .valid,
                pasteboard: pasteboard
            )
            let result = await injector.inject("Text", into: .target(target), timeout: .seconds(1))
            #expect(result.outcome == (target.isSecure ? .secureTarget : .noTarget))
            #expect(await pasteboard.metrics().snapshotCalls == 0)
        }
    }

    @Test("paste-command failure and missing completion are structured timeouts")
    func timeouts() async {
        let commandFailure = injector(
            target: validTarget,
            validation: .valid,
            pasteFails: true
        )
        #expect(
            await commandFailure.inject("Hello", into: .target(validTarget), timeout: .seconds(1))
                == InjectionResult(outcome: .timedOut, clipboardRestored: true)
        )

        let completionFailure = injector(
            target: validTarget,
            validation: .valid,
            completes: false
        )
        #expect(
            await completionFailure.inject("Hello", into: .target(validTarget), timeout: .seconds(1))
                == InjectionResult(outcome: .timedOut, clipboardRestored: true)
        )
    }

    @Test("a pre-cancelled task never touches the clipboard")
    func cancellation() async {
        let pasteboard = FakePasteboard()
        let injector = injector(
            target: validTarget,
            validation: .valid,
            pasteboard: pasteboard
        )
        let task = Task {
            await injector.inject("Hello", into: .target(validTarget), timeout: .seconds(1))
        }
        task.cancel()
        let result = await task.value

        #expect(result.outcome == .cancelled)
        #expect(await pasteboard.metrics().snapshotCalls == 0)
    }
}
