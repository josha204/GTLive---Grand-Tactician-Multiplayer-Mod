using UnityEngine;

namespace GTLive.Battle
{
    /// <summary>The battle part of the GT Live window.</summary>
    public static class BattleOverlay
    {
        public static void Draw(GUIStyle status, GUIStyle small)
        {
            switch (BattleLink.State)
            {
                case BattleLink.Phase.Launching: GUILayout.Label("Battle: loading the battlefield...", status); break;
                case BattleLink.Phase.Receiving: GUILayout.Label("Battle: receiving the battle from the host...", status); break;
                case BattleLink.Phase.Loading: GUILayout.Label("Battle: loading the battlefield...", status); break;
                case BattleLink.Phase.WaitingPeer: GUILayout.Label("Battle: waiting for the other player to arrive...", status); break;
                case BattleLink.Phase.Live: BattleSync.DrawOverlay(status, small); break;
            }
        }
    }
}
