import Testing
import FlowCore
@testable import FlowDictation

@Suite("SessionEventGate")
struct SessionEventGateTests {
    @Test("late events from a superseded session are rejected")
    func supersededSession() async {
        let gate = SessionEventGate()
        let first = DictationSessionID()
        let second = DictationSessionID()
        let firstGeneration = await gate.begin(first)
        let secondGeneration = await gate.begin(second)

        let stalePartialAccepted = await gate.acceptsPartial(
            session: first,
            generation: firstGeneration
        )
        let staleFinalClaimed = await gate.claimFinal(
            session: first,
            generation: firstGeneration
        )
        #expect(!stalePartialAccepted)
        #expect(!staleFinalClaimed)
        #expect(await gate.acceptsPartial(session: second, generation: secondGeneration))
    }

    @Test("a session final can be claimed exactly once")
    func exactlyOneFinal() async {
        let gate = SessionEventGate()
        let session = DictationSessionID()
        let generation = await gate.begin(session)

        #expect(await gate.claimFinal(session: session, generation: generation))
        let duplicateFinalClaimed = await gate.claimFinal(
            session: session,
            generation: generation
        )
        let partialAcceptedAfterFinal = await gate.acceptsPartial(
            session: session,
            generation: generation
        )
        #expect(!duplicateFinalClaimed)
        #expect(!partialAcceptedAfterFinal)
    }

    @Test("cancellation invalidates all later events")
    func cancellation() async {
        let gate = SessionEventGate()
        let session = DictationSessionID()
        let generation = await gate.begin(session)
        await gate.cancel(session)

        let partialAcceptedAfterCancellation = await gate.acceptsPartial(
            session: session,
            generation: generation
        )
        let finalClaimedAfterCancellation = await gate.claimFinal(
            session: session,
            generation: generation
        )
        #expect(!partialAcceptedAfterCancellation)
        #expect(!finalClaimedAfterCancellation)
    }
}
