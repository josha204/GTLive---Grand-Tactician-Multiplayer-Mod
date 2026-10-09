using System.IO;
using GTLive.Net;
using GTLive.Sync;
using HarmonyLib;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// The autoresolve panel's "Fight" button turns an engagement that's being autoresolved into a 3D battle.
    /// Between the two players that follows the same rule as the battle prompt: whoever pressed it commands their
    /// army in 3D, and the other player's army is run by the battle AI while its player watches (and can take
    /// command). The guest's game never launches a battle itself; it asks the host's.
    /// </summary>
    public static class BattleFight
    {
        public static bool InvolvesBoth(Autocalc a)
        {
            if (a?.envolvedunits == null) return false;
            bool host = false, guest = false;
            foreach (var side in a.envolvedunits)
            {
                if (side?.topunits == null) continue;
                foreach (var r in side.topunits)
                {
                    if (r == null) continue;
                    if (r.alliance == Mp.HostAlliance) host = true;
                    if (r.alliance == Mp.GuestAlliance) guest = true;
                }
            }
            return host && guest;
        }

        public static void Start(bool hostPlays, bool guestPlays)
        {
            ClockSync.SetShared(true, ClockSync.SharedSpeed);
            ClockSync.SendNow();
            BattleLink.Prepare(hostPlays, guestPlays);
            Session.SendNotice(BattleDecision.Roles(hostPlays, guestPlays));
        }

        /// <summary>Host: the guest pressed Fight on the engagement at this spot.</summary>
        public static void OnGuestFight(BinaryReader r)
        {
            var pos = r.ReadVector3();
            if (BattleLink.Active) return;
            if (Session.Battles != BattleMode.Ask)
            {
                Session.SendNotice("This session autoresolves all battles.");
                return;
            }
            var bfs = GameApi.BFS;
            if (bfs?.battlemonument == null) return;
            Autocalc best = null;
            float bestD = 6f;
            foreach (var m in bfs.battlemonument)
            {
                if (m == null || m.autocalcref == null || m.landorsea != 0) continue;
                float d = Tools.GetXZDistance(m.transform.position, pos);
                if (d < bestD) { bestD = d; best = m.autocalcref; }
            }
            if (best == null || !InvolvesBoth(best))
            {
                Session.SendNotice("That engagement can't be fought in 3D any more.");
                return;
            }
            Session.SendNotice(Session.RemoteName + " chose to fight the engagement in 3D.");
            Start(hostPlays: false, guestPlays: true);
            best.StartManualBattleFromAutocalc(launchedbyplayer: false);
        }
    }

    [HarmonyPatch(typeof(Autocalc), nameof(Autocalc.StartManualBattleFromAutocalc))]
    internal static class FightButton
    {
        private static bool Prefix(Autocalc __instance)
        {
            if (!Mp.Active || !Session.IsLive || !GameApi.InCampaignScene) return true;
            if (Mp.IsGuest)
            {
                var mon = __instance.GetComponent<BattleMonument>();
                var pos = mon != null ? mon.transform.position : __instance.transform.position;
                Session.Send(MsgType.BattleFight, Wire.Build(w => w.Write(pos)));
                Session.Notice("Asking " + Session.RemoteName + "'s game to start the battle in 3D...");
                return false;
            }
            if (BattleLink.Active || Session.Battles != BattleMode.Ask || !BattleFight.InvolvesBoth(__instance)) return true;
            BattleFight.Start(hostPlays: true, guestPlays: false);
            return true;
        }
    }
}
