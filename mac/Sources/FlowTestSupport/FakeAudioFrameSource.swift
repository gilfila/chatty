import Foundation
import AVFAudio
import FlowCore

/// Scripted `AudioFrameSource` for M2 tests. Owner: Gil — this is the mock the delivery plan makes
/// a dependency of Tony S's M2 work, so M2 never needs the real microphone or my M1 code.
///
/// Emits `frameCount` buffers of silence in the built-in mic's real shape (48kHz mono float),
/// then either finishes cleanly or throws `failWith`.
public final class FakeAudioFrameSource: AudioFrameSource, @unchecked Sendable {
    public let captureFormat: AVAudioFormat
    private let frameCount: Int
    private let framesPerBuffer: AVAudioFrameCount
    private let failWith: AudioSourceError?
    private let lock = NSLock()
    private var capturing = false
    private var continuation: AsyncThrowingStream<AudioFrame, any Error>.Continuation?

    public var isCapturing: Bool {
        get async { lock.withLock { capturing } }
    }

    /// Number of buffers actually yielded — lets a test assert capture really stopped on key-up.
    public private(set) var yieldedFrames = 0

    public init(
        sampleRate: Double = 48_000,
        channels: AVAudioChannelCount = 1,
        frameCount: Int = 10,
        framesPerBuffer: AVAudioFrameCount = 4_800,
        failWith: AudioSourceError? = nil
    ) {
        self.captureFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: sampleRate,
            channels: channels,
            interleaved: false
        )!
        self.frameCount = frameCount
        self.framesPerBuffer = framesPerBuffer
        self.failWith = failWith
    }

    public func startCapture(
        session: DictationSessionID
    ) async throws -> AsyncThrowingStream<AudioFrame, any Error> {
        if let failWith { throw failWith }
        lock.withLock { capturing = true }

        let (stream, continuation) = AsyncThrowingStream<AudioFrame, any Error>.makeStream()
        lock.withLock { self.continuation = continuation }

        let format = captureFormat
        let total = frameCount
        let perBuffer = framesPerBuffer

        Task { [weak self] in
            for index in 0..<total {
                guard let self, await self.isCapturing else { break }
                guard let buffer = AVAudioPCMBuffer(
                    pcmFormat: format,
                    frameCapacity: perBuffer
                ) else { break }
                buffer.frameLength = perBuffer
                continuation.yield(
                    AudioFrame(
                        session: session,
                        buffer: buffer,
                        hostTime: UInt64(index) * UInt64(perBuffer)
                    )
                )
                self.lock.withLock { self.yieldedFrames += 1 }
                await Task.yield()
            }
            continuation.finish()
        }

        return stream
    }

    public func stopCapture() async {
        let continuation = lock.withLock { () -> AsyncThrowingStream<AudioFrame, any Error>.Continuation? in
            capturing = false
            defer { self.continuation = nil }
            return self.continuation
        }
        // Contract: the stream must always finish so a consumer awaiting it is released.
        continuation?.finish()
    }
}
