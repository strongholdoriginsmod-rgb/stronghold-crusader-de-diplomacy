using System;
using StrongholdEuropeDE.Runtime;

static void Require(bool condition, string detail)
{
    if (!condition)
        throw new InvalidOperationException(detail);
}

var ownership = new PublicMissionSaveAttempt();
Require(ownership.TryAcceptStart(1, 1, false, false), "new map should own the first session");
Require(ownership.TryBegin(2), "save attempt should become pending");
Require(!ownership.TryAcceptStart(1, 1, false, false), "late start cannot replace a newer attempt");
Require(!ownership.Retire(1) && ownership.Pending == 2, "late end cannot discard the pending save");
Require(!ownership.TryAcceptStart(2, 2, true, false), "save start requires an observed native save");
Require(ownership.Retire(2) && ownership.Pending == 0, "failed load retires its own pending scope");
Require(!ownership.TryBegin(2), "late initialization cannot rearm a failed attempt");
Require(ownership.TryBegin(3), "replacement save should start independently");
Require(!ownership.TryAcceptStart(3, 2, true, true), "shared Current must match the notification");
Require(ownership.TryAcceptStart(3, 3, true, true) && ownership.Pending == 0, "current save has one owner");
Require(!ownership.TryAcceptStart(3, 3, true, true), "duplicate start cannot consume the save again");

var original = new OriginalSavedWorldReadiness();
var old = original.OpenOriginalLoad(1);
original.SavePre(old, 1);
original.Retire();
original.EndOriginalLoad(old, true);
Require(!original.IsAppliedForPayload(1, old.Serial), "late native producer cannot revive a retired load");
var current = original.OpenOriginalLoad(2);
original.SavePre(current, 2);
original.SavePost(current, 2);
original.CompleteManagedApply(original.BeginManagedApply(current));
Require(!original.IsAppliedForPayload(2, current.Serial), "managed apply alone cannot commit the restore");
Require(original.BeginManagedApply(current) == null, "completed managed apply cannot be consumed twice");
original.EndOriginalLoad(current, true);
Require(original.IsAppliedForPayload(2, current.Serial), "one original successful producer proves restore");
Require(!original.IsAppliedForPayload(2, old.Serial), "older payload cannot borrow successful proof");

Require(!OriginalHumanAllyOrderRules.HasPositiveCommonTeam(0, 0), "neutral equality is not alliance");
Require(!OriginalHumanAllyOrderRules.HasPositiveCommonTeam(1, 2), "different teams cannot issue an allied order");
Require(OriginalHumanAllyOrderRules.HasPositiveCommonTeam(2, 2), "matching positive team can proceed");
Require(HumanAllyMilitaryRequest.TryCreate(1, 2, 1, 3, out var order, out _), "valid attack tuple");
Require(order.MatchesOperands(1, 2, 3, 1, 2, 3), "unchanged operands pass");
Require(!order.MatchesOperands(1, 2, 3, 1, 2, 4), "foreign Pre cannot redirect the attack");
Require(!order.MatchesOperands(1, 4, 3, 1, 2, 3), "foreign Pre cannot launder the original recipient");
Require(!order.MatchesOperands(1, 2, 3, 2, 2, 3), "foreign Pre cannot change the order kind");
var route = new OriginalHumanAllyRouteFacts
{
    NativeLocalPlayer = 1,
    RequesterHuman = true,
    RequesterAI = false,
    RequesterAlive = true,
    ConsultedHuman = false,
    ConsultedAI = true,
    ConsultedAlive = true,
    RequesterAlliedToConsulted = true,
    ConsultedAlliedToRequester = true,
    TargetAlive = true,
    RequesterAlliedToTarget = false,
    TargetAlliedToRequester = false,
    ConsultedAlliedToTarget = false,
    TargetAlliedToConsulted = false
};
Require(OriginalHumanAllyOrderRules.ValidateRoute(order, route, out _), "live human may command allied AI against a live enemy");
var invalidRoute = route;
invalidRoute.NativeLocalPlayer = 2;
Require(!OriginalHumanAllyOrderRules.ValidateRoute(order, invalidRoute, out _), "changed native viewpoint invalidates the requester");
invalidRoute = route;
invalidRoute.ConsultedAI = false;
Require(!OriginalHumanAllyOrderRules.ValidateRoute(order, invalidRoute, out _), "non-AI ally cannot receive replay");
invalidRoute = route;
invalidRoute.TargetAlliedToConsulted = true;
Require(!OriginalHumanAllyOrderRules.ValidateRoute(order, invalidRoute, out _), "allied target cannot receive an attack");
int claim = 0;
Require(OriginalHumanAllyOrderRules.TryClaimDispatch(ref claim), "first replay owns the ticket");
Require(!OriginalHumanAllyOrderRules.TryClaimDispatch(ref claim), "duplicate replay cannot dispatch");
object currentReply = new object();
object expectedReply = currentReply;
Require(!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, expectedReply, 8, 8, null) &&
    ReferenceEquals(currentReply, expectedReply), "missing mission clock preserves the pending reply");
Require(!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, expectedReply, 8, 8, 255) &&
    ReferenceEquals(currentReply, expectedReply), "255 elapsed ticks do not expire the reply");
Require(!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, new object(), 8, 8, 256) &&
    ReferenceEquals(currentReply, expectedReply), "a replaced attempt cannot claim another reply");
Require(OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, expectedReply, 8, 8, 256) &&
    currentReply == null, "256 elapsed ticks atomically claim and clear the reply");
Require(!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, expectedReply, 8, 8, 256),
    "a second expiry cannot publish the same reply");
currentReply = expectedReply;
Require(!OriginalHumanAllyOrderRules.TryClaimReplyTimeout(ref currentReply, expectedReply, 8, 9, null) &&
    currentReply == null, "a replacement session retires the old reply without publishing a timeout");
Console.WriteLine("Diplomacy mission ownership, save proof and ally command contracts passed.");
