using System.IO;
using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>
    /// Cease fire. In single player, proposing one (and confirming) ends the day early; the AI goes along. Between
    /// two players the other one has to agree: the proposal shows up in their GT Live window with Accept and
    /// Decline. If the other army is run by the AI (its player is spectating), it's accepted at once, as in single
    /// player. Either way the host's game then ends the day, and the night follows for both.
    /// </summary>
    public static class BattleCeaseFire
    {
        /// <summary>A proposal from the other player waiting for our answer.</summary>
        public static bool IncomingProposal;
        /// <summary>We proposed and are waiting for the other player.</summary>
        public static bool OutgoingProposal;
        /// <summary>Host: both agreed; the day may end.</summary>
        public static bool Agreed;
        private static float _proposedAt;

        public static void Reset()
        {
            IncomingProposal = OutgoingProposal = Agreed = false;
        }

        /// <summary>Host, from CheckTimeIssues: the host's player confirmed a cease fire. Returns true if the day may end now.</summary>
        public static bool HostWantsToEndDay()
        {
            if (Agreed) return true;
            if (!BattleRules.GuestCommands)
            {
                Agreed = true;
                Session.SendNotice("Cease fire: the guns fall silent until morning.");
                return true;
            }
            if (!OutgoingProposal)
            {
                OutgoingProposal = true;
                _proposedAt = Time.unscaledTime;
                BattleSync.SendEvent(BattleSync.EvCeaseFire);
                Session.Notice("Cease fire proposed. Waiting for " + Session.RemoteName + " to answer...");
            }
            return false;
        }

        /// <summary>Guest: our player confirmed a cease fire (the game set triggerendofday, which only the host acts on).</summary>
        public static void GuestProposes()
        {
            if (OutgoingProposal) return;
            OutgoingProposal = true;
            _proposedAt = Time.unscaledTime;
            BattleSync.SendEvent(BattleSync.EvCeaseFire);
            Session.Notice("Cease fire proposed. Waiting for " + Session.RemoteName + " to answer...");
        }

        /// <summary>The other player proposes a cease fire.</summary>
        public static void OnProposal()
        {
            if (Mp.Role == Role.Host && !BattleRules.HostCommands)
            {
                // the host's army is run by the AI: it agrees, as in single player
                Answer(true);
                return;
            }
            IncomingProposal = true;
            Overlay.Visible = true;
            Session.Notice(Session.RemoteName + " proposes a cease fire until morning. Answer in the GT Live window (F8).");
        }

        /// <summary>Our player answers the other player's proposal.</summary>
        public static void Answer(bool accept)
        {
            IncomingProposal = false;
            if (Mp.Role == Role.Host)
            {
                // the guest proposed; the host's game ends the day
                BattleSync.SendEvent(BattleSync.EvCeaseFireAnswer, w => w.Write(accept));
                if (accept) Accepted();
                else Session.Notice("You declined the cease fire.");
            }
            else
            {
                BattleSync.SendEvent(BattleSync.EvCeaseFireAnswer, w => w.Write(accept));
                Session.Notice(accept ? "You accepted the cease fire." : "You declined the cease fire.");
            }
        }

        /// <summary>The other player answered our proposal.</summary>
        public static void OnAnswer(BinaryReader r)
        {
            bool accept = r.ReadBoolean();
            OutgoingProposal = false;
            if (!accept)
            {
                Session.Notice(Session.RemoteName + " declined the cease fire. The battle goes on.");
                return;
            }
            if (Mp.Role == Role.Host) Accepted();
            else Session.Notice(Session.RemoteName + " accepted the cease fire.");
        }

        /// <summary>Host: end the day (CheckTimeIssues takes it from here, and the guest follows the night as usual).</summary>
        private static void Accepted()
        {
            Agreed = true;
            OutgoingProposal = false;
            var bu = GameApi.BU;
            if (bu != null) bu.triggerendofday = true;
            Session.SendNotice("Cease fire agreed: the guns fall silent until morning.");
        }

        public static void HostTick()
        {
            var bu = GameApi.BU;
            // the agreement lasts until the game has acted on it
            if (Agreed && bu != null && !bu.triggerendofday && bu.eodcycle == 0 && Time.unscaledTime - _proposedAt > 5f) Agreed = false;
        }

        public static void DrawOverlay(GUIStyle status)
        {
            if (IncomingProposal)
            {
                GUILayout.Label(Session.RemoteName + " proposes a cease fire until morning.", status);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Accept cease fire")) Answer(true);
                if (GUILayout.Button("Decline")) Answer(false);
                GUILayout.EndHorizontal();
            }
            else if (OutgoingProposal)
                GUILayout.Label("Cease fire proposed; waiting for " + Session.RemoteName + ".", status);
        }
    }
}
