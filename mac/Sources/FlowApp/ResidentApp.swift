import AppKit
import Foundation
import FlowCore
import FlowDictation

// ResidentApp — the menu-bar shell. Owner: Gil (M1 shell, M3 integration).
//
// The full product loop: hold right Option → the focused field is captured and live audio
// streams into AppleSpeechTranscriber while the HUD shows partial text; release → the session
// finalizes, the transcript is formatted, persisted, and pasted into the captured field via
// SafeTextInjector; Escape cancels. Persist-before-inject is invariant 2 of the FlowCore state
// machine: an injection failure is recoverable ("Copy Last Transcript"), never data loss.
//
// Every transition appends a JSON line to ~/Library/Application Support/Flow/resident.log so
// resident behavior is verifiable without a human watching the screen.
@MainActor
final class ResidentApp: NSObject, NSApplicationDelegate, DictationSessionDelegate {
    static let triggerKeyName = "right ⌥ (Option)"

    private let trigger = HotkeyDictationTrigger(
        triggerKeycode: HotkeyDictationTrigger.rightOptionKeycode
    )
    private let audioSource = LiveAudioFrameSource()
    private let transcriber = AppleSpeechTranscriber()
    private let formatter = DeterministicFormatter()
    private let inspector = AXFocusTargetInspector()
    private let injector: SafeTextInjector
    private let transcripts: LocalTranscriptStore
    private let dictionary: LocalDictionaryStore
    private var coordinator: DictationCoordinator!

    private var statusItem: NSStatusItem?
    private var stateMenuItem: NSMenuItem?
    private var copyLastMenuItem: NSMenuItem?
    private var hud: NSPanel?
    private var hudLabel: NSTextField?

    private var eventPump: Task<Void, Never>?
    /// The one live session (state-machine invariant 4). The task consumes the transcription
    /// stream through injection; it outlives `endSession` because the final arrives after
    /// `finish()` is called.
    private var activeSessionID: DictationSessionID?
    private var coordinatorSessionID: DictationSessionID?
    private var requestedEnd: SessionEndRequest?
    private var pendingOutcome: InjectionOutcome?
    private var pendingFailure: DictationFailure?
    /// Bumped on every state change so a delayed "flash then idle" only fires if nothing newer
    /// took over the HUD in the meantime.
    private var hudGeneration = 0
    private var lastFinalText: String?

    override init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Flow", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        injector = SafeTextInjector(
            targets: inspector,
            pasteboard: SystemPasteboard(),
            pasteCommand: CGPasteCommandPoster(),
            completion: BoundedWaitPasteCompletion()
        )
        transcripts = LocalTranscriptStore(fileURL: dir.appendingPathComponent("transcripts.json"))
        dictionary = LocalDictionaryStore(fileURL: dir.appendingPathComponent("dictionary.json"))
        super.init()
        coordinator = DictationCoordinator(
            transcriber: transcriber,
            formatter: formatter,
            transcripts: transcripts,
            dictionary: dictionary,
            injector: injector,
            delegate: self,
            injectionTimeout: .milliseconds(300)
        )
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        setUpStatusItem()
        setUpHUD()
        log("launched", detail: Bundle.main.bundleIdentifier ?? "?")

        Task { [weak self, transcripts] in
            guard let transcript = try? await transcripts.lastSuccessfulTranscript() else { return }
            self?.lastFinalText = transcript.formattedText
            self?.copyLastMenuItem?.isEnabled = true
        }

        eventPump = Task { [weak self] in
            guard let self else { return }
            do {
                try await trigger.start()
            } catch {
                log("trigger-start-failed", detail: String(describing: error))
                showState(.recoverableFailure(.permissionDenied(.inputMonitoring)))
                return
            }
            log("trigger-started", detail: "\(Self.triggerKeyName), listen-only tap")
            for await event in trigger.events {
                switch event {
                case .pressed: beginSession()
                case .released: endSession(cancelled: false)
                case .cancelled: endSession(cancelled: true)
                }
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        log("terminating", detail: "")
    }

    // MARK: - Session loop

    private enum SessionEndRequest {
        case finish
        case cancel
    }

    private func beginSession() {
        guard activeSessionID == nil else { return }
        let session = DictationSessionID()
        activeSessionID = session
        coordinatorSessionID = nil
        requestedEnd = nil
        pendingOutcome = nil
        pendingFailure = nil
        log("pressed", detail: session.description)
        showState(.listening(session))

        // Safety cap: if the release is ever lost (tap glitch, key swallowed by a secure input
        // field), the mic must not run forever. Finalize as if released.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(90))
            await MainActor.run { [weak self] in
                guard let self,
                      activeSessionID == session,
                      requestedEnd == nil
                else { return }
                log("listening-timeout", detail: session.description)
                endSession(cancelled: false)
            }
        }

        Task { [weak self] in
            guard let self else { return }
            do {
                // DictationCoordinator owns target capture, transcription consumption,
                // persistence, and injection as one tested safety pipeline.
                try await coordinator.start(session: session, source: audioSource)
                guard activeSessionID == session else {
                    await coordinator.cancel(session: session)
                    return
                }
                coordinatorSessionID = session

                // A very quick key release can arrive while the speech framework is still
                // starting. Replaying the queued edge here closes that race without allowing a
                // second finalization call for an already-started session.
                if let requestedEnd {
                    await apply(requestedEnd, to: session)
                }
            } catch {
                log("session-start-failed", detail: String(describing: error))
            }
        }
    }

    private func endSession(cancelled: Bool) {
        guard let session = activeSessionID, requestedEnd == nil else { return }
        let request: SessionEndRequest = cancelled ? .cancel : .finish
        requestedEnd = request

        switch request {
        case .cancel:
            log("cancelled", detail: session.description)
            showState(.cancelled(session))
        case .finish:
            log("released", detail: session.description)
            showState(.finalizing(session))
        }

        if coordinatorSessionID == session {
            Task { [weak self] in
                await self?.apply(request, to: session)
            }
        }
    }

    private func apply(_ request: SessionEndRequest, to session: DictationSessionID) async {
        switch request {
        case .finish:
            await coordinator.finish(session: session)
        case .cancel:
            await coordinator.cancel(session: session)
        }
    }

    // MARK: - DictationSessionDelegate

    func dictationSession(_ id: DictationSessionID, didChangeState state: DictationState) {
        guard activeSessionID == id else {
            log("stale-state", detail: "\(id): \(state)")
            return
        }

        switch state {
        case .listening where requestedEnd != nil:
            // The key was released while the speech framework was starting. Keep the HUD in the
            // queued terminal state until `beginSession` replays that edge into the coordinator.
            log("state-suppressed", detail: "\(id): \(state)")

        case .idle:
            let outcome = pendingOutcome
            let failure = pendingFailure
            completeSession(id)
            if let failure {
                present(failure)
            } else if let outcome {
                present(outcome)
            } else {
                showState(.idle)
            }

        case .recoverableFailure(let failure):
            let callbackFailure = pendingFailure
            let reportedFailure = callbackFailure ?? failure
            let outcome = pendingOutcome
            completeSession(id)
            if let callbackFailure {
                present(callbackFailure)
            } else if case .injectionFailed(let stateOutcome) = reportedFailure {
                present(outcome ?? stateOutcome)
            } else {
                present(reportedFailure)
            }

        default:
            showState(state)
        }
    }

    func dictationSession(_ id: DictationSessionID, didProducePartial text: String) {
        guard activeSessionID == id, requestedEnd == nil else { return }
        showPartial(text)
    }

    func dictationSession(_ id: DictationSessionID, didFinalize transcript: Transcript) {
        guard activeSessionID == id else { return }
        lastFinalText = transcript.formattedText
        copyLastMenuItem?.isEnabled = true
        log("final", detail: transcript.formattedText)
    }

    func dictationSession(_ id: DictationSessionID, didAttemptInjection outcome: InjectionOutcome) {
        guard activeSessionID == id else { return }
        pendingOutcome = outcome
        log("injection", detail: outcome.rawValue)
    }

    func dictationSession(_ id: DictationSessionID, didFail failure: DictationFailure) {
        guard activeSessionID == id else { return }
        pendingFailure = failure
        log("session-failed", detail: String(describing: failure))

        // If durable storage itself failed, the coordinator deliberately retained the final in
        // memory and did not paste it. Surface that text through Copy Last without weakening the
        // persist-before-inject rule.
        if case .internalError = failure {
            Task { [weak self] in
                guard let self,
                      let transcript = await coordinator.recoverableTranscript(),
                      activeSessionID == nil || activeSessionID == id
                else { return }
                lastFinalText = transcript.formattedText
                copyLastMenuItem?.isEnabled = true
            }
        }
    }

    private func completeSession(_ id: DictationSessionID) {
        guard activeSessionID == id else { return }
        activeSessionID = nil
        coordinatorSessionID = nil
        requestedEnd = nil
        pendingOutcome = nil
        pendingFailure = nil
    }

    private func present(_ outcome: InjectionOutcome) {
        switch outcome {
        case .inserted:
            flash("✓ Inserted", thenIdleAfter: .seconds(1))
        case .secureTarget:
            flash("✕ Password field — nothing saved or inserted", thenIdleAfter: .seconds(2))
        case .cancelled:
            flash("✕ Cancelled — use Copy Last Transcript", thenIdleAfter: .seconds(3))
        case .permissionDenied:
            flash("✕ Accessibility needed — use Copy Last Transcript", thenIdleAfter: .seconds(3))
        case .noTarget, .targetChanged, .timedOut:
            flash("✕ Couldn't insert — use Copy Last Transcript", thenIdleAfter: .seconds(3))
        }
    }

    private func present(_ failure: DictationFailure) {
        switch failure {
        case .permissionDenied(let permission):
            flash("✕ Allow \(permission) in System Settings", thenIdleAfter: .seconds(3))
        case .audioUnavailable:
            flash("✕ Microphone unavailable", thenIdleAfter: .seconds(2))
        case .transcriptionFailed(let detail)
            where detail.contains("emptyFinal")
                || detail.localizedCaseInsensitiveContains("no speech"):
            flash("No speech heard", thenIdleAfter: .milliseconds(1500))
        case .transcriptionFailed:
            flash("✕ Transcription failed", thenIdleAfter: .seconds(2))
        case .injectionFailed(let outcome):
            present(outcome)
        case .internalError(let detail)
            where detail.localizedCaseInsensitiveContains("result could not be saved"):
            flash("⚠ Inserted, but status was not saved — Copy Last is available", thenIdleAfter: .seconds(3))
        case .internalError:
            flash("✕ Couldn't save safely — text was not inserted; use Copy Last", thenIdleAfter: .seconds(3))
        }
    }

    // MARK: - Status item + HUD

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = NSImage(systemSymbolName: "mic", accessibilityDescription: "Flow")

        let menu = NSMenu()
        let state = NSMenuItem(title: Self.idleMenuTitle, action: nil, keyEquivalent: "")
        state.isEnabled = false
        menu.addItem(state)
        menu.addItem(.separator())
        let copyLast = NSMenuItem(
            title: "Copy Last Transcript",
            action: #selector(copyLastTranscript),
            keyEquivalent: "c"
        )
        copyLast.target = self
        copyLast.isEnabled = false
        menu.addItem(copyLast)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit Flow", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        menu.autoenablesItems = false
        item.menu = menu

        statusItem = item
        stateMenuItem = state
        copyLastMenuItem = copyLast
    }

    @objc private func copyLastTranscript() {
        guard let lastFinalText else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(lastFinalText, forType: .string)
    }

    private func setUpHUD() {
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 340, height: 48),
            styleMask: [.nonactivatingPanel, .hudWindow, .titled],
            backing: .buffered,
            defer: true
        )
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.level = .floating
        // This is a transient, status-like surface rather than a second text-entry area. Keep
        // it anchored below the menu-bar clock, clear of the browser's address bar and lower
        // third where people commonly type.
        panel.isMovableByWindowBackground = false
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        let label = NSTextField(labelWithString: "● Listening…")
        label.font = .systemFont(ofSize: 15, weight: .medium)
        label.alignment = .center
        label.lineBreakMode = .byTruncatingHead
        label.frame = panel.contentView?.bounds ?? .zero
        label.autoresizingMask = [.width, .height]
        panel.contentView?.addSubview(label)

        if let screen = NSScreen.main {
            let frame = screen.visibleFrame
            panel.setFrameOrigin(NSPoint(
                x: frame.maxX - panel.frame.width - 18,
                y: frame.maxY - panel.frame.height - 18
            ))
        }

        hud = panel
        hudLabel = label
    }

    private static let idleMenuTitle = "Idle — hold \(ResidentApp.triggerKeyName) to dictate, Esc cancels"

    private func showPartial(_ text: String) {
        guard activeSessionID != nil, !text.isEmpty else { return }
        // `AppleSpeechTranscriber` publishes each provisional recognition as it arrives. Render
        // the tail immediately, so the newest words stay visible without waiting for key-up.
        hudLabel?.stringValue = "● " + String(text.suffix(56))
    }

    private func showState(_ state: DictationState) {
        hudGeneration += 1
        switch state {
        case .listening:
            statusItem?.button?.image = NSImage(systemSymbolName: "mic.fill", accessibilityDescription: "Flow — listening")
            stateMenuItem?.title = "Listening…"
            hudLabel?.stringValue = "● Listening…"
            hud?.orderFrontRegardless()
        case .finalizing, .inserting:
            statusItem?.button?.image = NSImage(systemSymbolName: "mic.fill", accessibilityDescription: "Flow — working")
            stateMenuItem?.title = "Working…"
            hudLabel?.stringValue = "…"
            hud?.orderFrontRegardless()
        case .recoverableFailure(let failure):
            statusItem?.button?.image = NSImage(systemSymbolName: "mic.slash", accessibilityDescription: "Flow — needs attention")
            stateMenuItem?.title = "Needs attention: \(failure)"
            hud?.orderOut(nil)
        default:
            statusItem?.button?.image = NSImage(systemSymbolName: "mic", accessibilityDescription: "Flow")
            stateMenuItem?.title = Self.idleMenuTitle
            hud?.orderOut(nil)
        }
        log("state", detail: String(describing: state))
    }

    /// Shows a short result message in the HUD, then returns to idle unless a newer state change
    /// (e.g. the next hold) has taken the HUD over.
    private func flash(_ message: String, thenIdleAfter delay: Duration) {
        hudGeneration += 1
        let generation = hudGeneration
        hudLabel?.stringValue = message
        hud?.orderFrontRegardless()
        Task { [weak self] in
            try? await Task.sleep(for: delay)
            await MainActor.run { [weak self] in
                guard let self, hudGeneration == generation, activeSessionID == nil else { return }
                showState(.idle)
            }
        }
    }

    // MARK: - Evidence log

    private func log(_ event: String, detail: String) {
        struct Line: Codable {
            var ts: String
            var event: String
            var detail: String
        }
        let line = Line(
            ts: ISO8601DateFormatter().string(from: Date()),
            event: event,
            detail: detail
        )
        guard let data = try? JSONEncoder().encode(line) else { return }
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Flow", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("resident.log")
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: data + Data("\n".utf8))
        } else {
            try? (data + Data("\n".utf8)).write(to: url)
        }
    }
}

/// Entry point for resident mode (a plain `open Flow.app` with no arguments).
@MainActor
func runResidentApp() {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let delegate = ResidentApp()
    app.delegate = delegate
    app.run()
}
