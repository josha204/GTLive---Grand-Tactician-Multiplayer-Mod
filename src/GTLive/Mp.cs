namespace GTLive
{
    public enum Role { None, Host, Guest }

    /// <summary>Multiplayer rules the Harmony patches consult. Plain statics so patched game code can call them cheaply.</summary>
    public static class Mp
    {
        /// <summary>True from the moment a session starts loading its synced save until the session ends.</summary>
        public static bool Active;
        public static Role Role = Role.None;
        public static int HostAlliance = 0;
        public static int GuestAlliance = 1;

        public static bool IsHost => Active && Role == Role.Host;
        public static bool IsGuest => Active && Role == Role.Guest;

        public static int LocalAlliance => Role == Role.Guest ? GuestAlliance : HostAlliance;
        public static int RemoteAlliance => Role == Role.Guest ? HostAlliance : GuestAlliance;

        /// <summary>Set while applying something that came from the other player, so capture patches don't echo it back.</summary>
        public static bool Applying;

        /// <summary>
        /// Only the campaign map has two humans. In a tactical battle the opposing army is run by the battle AI
        /// as usual, so the human rules switch off there.
        /// </summary>
        public static bool CampaignRules => Active && GameApi.InCampaignScene;

        /// <summary>
        /// Where both humans are present: the campaign map and a shared 3D battle. In a battle against the AI
        /// (only one player in it) the usual single player rules apply.
        /// </summary>
        public static bool HumanRules => CampaignRules || Battle.BattleRules.InMpBattle;

        public static bool IsHuman(int alliance)
        {
            if (!CampaignRules) return alliance == GameVars.playeralliance && !GameVars.ai_vs_ai;
            return alliance == HostAlliance || alliance == GuestAlliance;
        }

        /// <summary>
        /// Replacement for "x == GameVars.playeralliance" in AI decision code (see HumanPatches).
        /// Outside a session it returns playeralliance, so the comparison is unchanged.
        /// In a session it returns x itself for either human alliance (comparison true) and an impossible id otherwise.
        /// </summary>
        public static int MatchHuman(int x)
        {
            if (!HumanRules) return GameVars.playeralliance;
            return (x == HostAlliance || x == GuestAlliance) ? x : int.MinValue;
        }

        /// <summary>The other player's "auto-manage" choices, applied while the host runs AI routines for their alliance.</summary>
        public static readonly AutoPrefs RemotePrefs = new AutoPrefs();
    }

    public class AutoPrefs
    {
        public bool Finances, ConstructionP, ConstructionG, ConstructionR, Weapons, Projects, Policies, PlaceBuildings;

        public static AutoPrefs FromGame() => new AutoPrefs
        {
            Finances = GameVars.automanagefinances,
            ConstructionP = GameVars.automanageconstructionp,
            ConstructionG = GameVars.automanageconstructiong,
            ConstructionR = GameVars.automanageconstructionr,
            Weapons = GameVars.automanageweapons,
            Projects = GameVars.automanageprojects,
            Policies = GameVars.automanagepolicies,
            PlaceBuildings = GameVars.autoplacebuildings,
        };

        public void ApplyToGame()
        {
            GameVars.automanagefinances = Finances;
            GameVars.automanageconstructionp = ConstructionP;
            GameVars.automanageconstructiong = ConstructionG;
            GameVars.automanageconstructionr = ConstructionR;
            GameVars.automanageweapons = Weapons;
            GameVars.automanageprojects = Projects;
            GameVars.automanagepolicies = Policies;
            GameVars.autoplacebuildings = PlaceBuildings;
        }

        public void CopyFrom(AutoPrefs o)
        {
            Finances = o.Finances; ConstructionP = o.ConstructionP; ConstructionG = o.ConstructionG; ConstructionR = o.ConstructionR;
            Weapons = o.Weapons; Projects = o.Projects; Policies = o.Policies; PlaceBuildings = o.PlaceBuildings;
        }

        public bool SameAs(AutoPrefs o) =>
            Finances == o.Finances && ConstructionP == o.ConstructionP && ConstructionG == o.ConstructionG && ConstructionR == o.ConstructionR &&
            Weapons == o.Weapons && Projects == o.Projects && Policies == o.Policies && PlaceBuildings == o.PlaceBuildings;
    }
}
