using System.Numerics;

namespace FractureTogether;

// Pure policy only. The Unity adapter supplies a single cabin's local point,
// authored collider center/half-size and world-to-local axis norms. Disabled
// collider.bounds must not be used as cabin geometry.
public static class ElevatorPresenceRules
{
    public const float ToleranceWorld = .15f;
    public const float MaximumStateAge = 3f;

    public static bool ContainsLocal(Vector3 point, Vector3 center, Vector3 halfSize,
        Vector3 inverseWorldAxisNorms, float toleranceWorld = ToleranceWorld)
    {
        if (!Finite(point) || !Finite(center) || !PositiveFinite(halfSize) ||
            !PositiveFinite(inverseWorldAxisNorms) || !float.IsFinite(toleranceWorld) || toleranceWorld < 0)
            return false;

        // Compute in double so finite large input cannot overflow an expanded
        // float extent to Infinity and accidentally admit every position.
        return Axis(point.X, center.X, halfSize.X, inverseWorldAxisNorms.X, toleranceWorld) &&
               Axis(point.Y, center.Y, halfSize.Y, inverseWorldAxisNorms.Y, toleranceWorld) &&
               Axis(point.Z, center.Z, halfSize.Z, inverseWorldAxisNorms.Z, toleranceWorld);
    }

    static bool Axis(float point, float center, float half, float inverseNorm, float tolerance) =>
        Math.Abs((double)point - center) <= (double)half + (double)tolerance * inverseNorm;
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    static bool PositiveFinite(Vector3 p) => Finite(p) && p.X > 0 && p.Y > 0 && p.Z > 0;

    // Down/Dead are explicit authority facts, never inferred from Health<=0.
    // A host-confirmed Down remains owned through loading/world changes.
    public readonly record struct MemberSnapshot(int Id, float AgeSeconds, bool WorldMatches,
        bool KnownAlive, bool ConfirmedDown, bool ConfirmedDead, int? CabinId);

    public enum PresenceFailure
    {
        None, PartyWiped, InvalidCabin, InvalidRoster, RosterCountMismatch,
        DuplicateRosterId, MissingMember, DuplicateMemberState, ContradictoryLifeState,
        UnknownLifeState, StaleState, WorldMismatch, OutsideSelectedCabin, NoLivingMembers
    }

    public readonly record struct Evaluation(bool Allowed, PresenceFailure Failure, int BlockingMemberId);

    public static Evaluation Evaluate(int expectedCount, IReadOnlyList<int>? authoritativeSessionIds,
        IReadOnlyList<MemberSnapshot>? snapshots, int selectedCabinId, bool partyWiped)
    {
        if (partyWiped) return Block(PresenceFailure.PartyWiped);
        if (selectedCabinId == 0) return Block(PresenceFailure.InvalidCabin);
        if (expectedCount < 1 || authoritativeSessionIds == null || snapshots == null)
            return Block(PresenceFailure.InvalidRoster);
        if (authoritativeSessionIds.Count != expectedCount)
            return Block(PresenceFailure.RosterCountMismatch);

        // Small authoritative session roster: deliberately no LINQ/allocations.
        // Validate the whole ID set before considering any terminal exclusions.
        for (var i = 0; i < authoritativeSessionIds.Count; i++)
        {
            var id = authoritativeSessionIds[i];
            if (id < 0) return Block(PresenceFailure.InvalidRoster, id);
            for (var j = 0; j < i; j++)
                if (authoritativeSessionIds[j] == id) return Block(PresenceFailure.DuplicateRosterId, id);
        }

        var living = 0;
        for (var memberIndex = 0; memberIndex < authoritativeSessionIds.Count; memberIndex++)
        {
            var id = authoritativeSessionIds[memberIndex];
            MemberSnapshot member = default;
            var found = false;
            for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
            {
                var candidate = snapshots[snapshotIndex];
                if (candidate.Id != id) continue; // Already-disconnected snapshots do not block.
                if (found) return Block(PresenceFailure.DuplicateMemberState, id);
                member = candidate;
                found = true;
            }
            if (!found) return Block(PresenceFailure.MissingMember, id);
            if (member.KnownAlive && (member.ConfirmedDown || member.ConfirmedDead))
                return Block(PresenceFailure.ContradictoryLifeState, id);
            if (member.ConfirmedDown) continue;
            if (!member.KnownAlive && !member.ConfirmedDead)
                return Block(PresenceFailure.UnknownLifeState, id);
            if (!float.IsFinite(member.AgeSeconds) || member.AgeSeconds < 0 || member.AgeSeconds > MaximumStateAge)
                return Block(PresenceFailure.StaleState, id);
            if (!member.WorldMatches) return Block(PresenceFailure.WorldMismatch, id);
            // An old dead snapshot alone cannot prove a peer has not revived in
            // another world. Only the explicit host Down fact skips these gates.
            if (member.ConfirmedDead) continue;
            living++;
            if (member.CabinId != selectedCabinId) return Block(PresenceFailure.OutsideSelectedCabin, id);
        }
        return living > 0 ? new(true, PresenceFailure.None, -1) : Block(PresenceFailure.NoLivingMembers);
    }

    static Evaluation Block(PresenceFailure reason, int id = -1) => new(false, reason, id);
}
