import Foundation
@preconcurrency import AVFAudio

/// One buffer of captured microphone audio.
///
/// `@unchecked Sendable` is deliberate and load-bearing: `AVAudioPCMBuffer` is a reference type
/// that AVFoundation hands us on a realtime thread. The ownership rule that makes this safe is —
/// **the producer must not retain or mutate a buffer after yielding it.** `LiveAudioFrameSource`
/// copies each tap buffer before yielding. Any other `AudioFrameSource` must do the same.
public struct AudioFrame: @unchecked Sendable {
    public let session: DictationSessionID
    public let buffer: AVAudioPCMBuffer
    /// Mach host time of the first sample, for latency measurement.
    public let hostTime: UInt64

    public init(session: DictationSessionID, buffer: AVAudioPCMBuffer, hostTime: UInt64) {
        self.session = session
        self.buffer = buffer
        self.hostTime = hostTime
    }
}

/// Microphone capture. Owner: Gil (M1). Consumer: Tony S's transcriber (M2).
///
/// Contract:
/// - `startCapture` is called once per session and returns a stream that yields until the source
///   is stopped, the session is cancelled, or capture fails.
/// - The stream finishes (does not hang) on `stopCapture()`, audio-route interruption, and app
///   termination. A consumer awaiting the stream will always be released.
/// - The stream throws `AudioSourceError` rather than yielding silence on failure.
/// - `captureFormat` is stable for the lifetime of one session. It is NOT guaranteed to be the
///   format the transcriber wants — this machine's built-in mic is 48kHz mono, so the consumer
///   owns any conversion.
public protocol AudioFrameSource: AnyObject, Sendable {
    var captureFormat: AVAudioFormat { get }
    var isCapturing: Bool { get async }

    func startCapture(session: DictationSessionID) async throws -> AsyncThrowingStream<AudioFrame, any Error>
    func stopCapture() async
}

public enum AudioSourceError: Error, Sendable, Equatable {
    case permissionDenied
    case noInputDevice
    case interrupted
    case engineFailed(String)
}
