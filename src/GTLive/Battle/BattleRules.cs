namespace GTLive.Battle
{
    /// <summary>
    /// Who gives the orders in a shared 3D battle. A side whose player chose "Play" (or took command later) is
    /// commanded by that player; any other side is run by the battle AI on the host while its player spectates.
    /// </summary>
    public static class BattleRules
    {
        /// <summary>True from the moment both players are headed into a shared battle until it is over.</summary>
        public static bool Active;
        public static bool HostCommands, GuestCommands;

        /// <summary>True while this game is running the 3D scene of a shared battle.</summary>
        public static bool InMpBattle => Active && Mp.Active && !GameApi.InCampaignScene && GameApi.BU != null;

        public static void Begin(bool hostPlays, bool guestPlays)
        {
            HostCommands = hostPlays;
            GuestCommands = guestPlays;
            Active = true;
        }

        public static void End()
        {
            Active = false;
            HostCommands = GuestCommands = false;
        }

        public static bool Commanding(int alliance)
        {
            if (alliance == Mp.HostAlliance) return HostCommands;
            if (alliance == Mp.GuestAlliance) return GuestCommands;
            return false;
        }

        public static bool LocalCommands => Commanding(Mp.LocalAlliance);
        public static bool RemoteCommands => Commanding(Mp.RemoteAlliance);

        public static void SetCommanding(int alliance, bool value)
        {
            if (alliance == Mp.HostAlliance) HostCommands = value;
            else if (alliance == Mp.GuestAlliance) GuestCommands = value;
        }

        /// <summary>
        /// Replacement for "x == GameVars.playeralliance" where the battle code means "is a human giving this
        /// side its orders?" (AI allocation, AI movement habits). Outside a shared battle it returns
        /// playeralliance, so the comparison is unchanged; inside one it is true only for a commanded side.
        /// </summary>
        public static int MatchCommander(int x)
        {
            if (!InMpBattle) return GameVars.playeralliance;
            return Commanding(x) ? x : int.MinValue;
        }
    }
}
