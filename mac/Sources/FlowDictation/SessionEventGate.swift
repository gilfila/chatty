import FlowCore

/// Rejects late framework events from cancelled or superseded sessions and claims one final.
public actor SessionEventGate {
    private struct ActiveSession: Sendable {
        let id: DictationSessionID
        let generation: UInt64
        var finalClaimed: Bool
    }

    private var nextGeneration: UInt64 = 0
    private var active: ActiveSession?

    public init() {}

    @discardableResult
    public func begin(_ id: DictationSessionID) -> UInt64 {
        nextGeneration &+= 1
        active = ActiveSession(id: id, generation: nextGeneration, finalClaimed: false)
        return nextGeneration
    }

    public func acceptsPartial(
        session id: DictationSessionID,
        generation: UInt64
    ) -> Bool {
        guard let active else { return false }
        return active.id == id
            && active.generation == generation
            && !active.finalClaimed
    }

    public func claimFinal(
        session id: DictationSessionID,
        generation: UInt64
    ) -> Bool {
        guard var active,
              active.id == id,
              active.generation == generation,
              !active.finalClaimed
        else {
            return false
        }

        active.finalClaimed = true
        self.active = active
        return true
    }

    public func cancel(_ id: DictationSessionID) {
        guard active?.id == id else { return }
        nextGeneration &+= 1
        active = nil
    }

    public func complete(_ id: DictationSessionID) {
        guard active?.id == id else { return }
        active = nil
    }
}
