import Foundation
@preconcurrency import AVFAudio
import AVFoundation
import FlowCore

// LiveAudioFrameSource — the real-microphone AudioFrameSource. Owner: Gil (M1).
//
// AVAudioEngine's input tap fires on a realtime audio thread; per the AudioFrame contract the
// buffer AVFoundation hands us there is copied before it is yielded, and never touched again by
// this producer. Everything else (engine lifecycle, tap install/remove, the one live
// continuation) is actor-isolated.
public actor LiveAudioFrameSource: AudioFrameSource {
    public nonisolated let captureFormat: AVAudioFormat

    private let engine: AVAudioEngine
    private var continuation: AsyncThrowingStream<AudioFrame, any Error>.Continuation?
    private var configChangeObserver: (any NSObjectProtocol)?

    public var isCapturing: Bool { continuation != nil }

    public init() {
        let engine = AVAudioEngine()
        self.engine = engine
        self.captureFormat = engine.inputNode.inputFormat(forBus: 0)
    }

    public func startCapture(
        session: DictationSessionID
    ) async throws -> AsyncThrowingStream<AudioFrame, any Error> {
        guard continuation == nil else {
            throw AudioSourceError.engineFailed("startCapture called while already capturing")
        }
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else {
            throw AudioSourceError.permissionDenied
        }
        // A machine with no input device reports a 0 Hz / 0-channel input format. Failing here
        // honors the never-yield-silence contract: the tap would install but never fire.
        guard captureFormat.sampleRate > 0, captureFormat.channelCount > 0 else {
            throw AudioSourceError.noInputDevice
        }

        let (stream, continuation) = AsyncThrowingStream<AudioFrame, any Error>.makeStream()
        self.continuation = continuation

        engine.inputNode.installTap(onBus: 0, bufferSize: 4096, format: captureFormat) { buffer, when in
            guard let copy = copySamples(of: buffer) else { return }
            continuation.yield(AudioFrame(session: session, buffer: copy, hostTime: when.hostTime))
        }

        // Device unplug / default-input change surfaces as a configuration change. The contract
        // says finish (throwing), never hang or go silent.
        configChangeObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { _ in
            Task { await self.finish(throwing: AudioSourceError.interrupted) }
        }

        do {
            try engine.start()
        } catch {
            teardown()
            self.continuation = nil
            throw AudioSourceError.engineFailed(error.localizedDescription)
        }

        continuation.onTermination = { _ in
            Task { await self.stopCapture() }
        }
        return stream
    }

    public func stopCapture() async {
        finish(throwing: nil)
    }

    private func finish(throwing error: (any Error)?) {
        guard let continuation else { return }
        self.continuation = nil
        teardown()
        if let error {
            continuation.finish(throwing: error)
        } else {
            continuation.finish()
        }
    }

    private func teardown() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        if let configChangeObserver {
            NotificationCenter.default.removeObserver(configChangeObserver)
            self.configChangeObserver = nil
        }
    }
}

/// Deep-copies the samples of a tap buffer. Free function because it runs on the realtime audio
/// thread, outside the actor.
private func copySamples(of buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
    guard buffer.frameLength > 0,
          let copy = AVAudioPCMBuffer(pcmFormat: buffer.format, frameCapacity: buffer.frameLength)
    else { return nil }
    copy.frameLength = buffer.frameLength
    let source = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
    let destination = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
    for (src, dst) in zip(source, destination) {
        guard let srcData = src.mData, let dstData = dst.mData else { return nil }
        memcpy(dstData, srcData, Int(src.mDataByteSize))
    }
    return copy
}
