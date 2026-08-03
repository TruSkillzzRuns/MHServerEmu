using MHServerEmu.Core.Config;

namespace MHServerEmu.Games.GameData
{
    public class GameDataConfig : ConfigContainer
    {
        public bool LoadAllPrototypes { get; private set; } = false;
        public bool UseEquipmentSlotTableCache { get; private set; } = false;
        public bool EnablePatchManager { get; private set; } = true;
        public bool EnableLiveTuningEvents { get; private set; } = true;
        public bool AutoRefreshLiveTuning { get; private set; } = true;
        public bool LoadLocaleFiles { get; private set; } = false;

        /// <summary>
        /// Optional absolute path to a Data\Game\Loco folder to load locale
        /// files from. Intended to point at the player's OWN game install
        /// (same idea as ClientAssets.CookedPCConsolePath) so real in-game
        /// item/power names resolve without any game data being copied into
        /// this repository or shipped with the app. Empty = use the server's
        /// own Data\Game\Loco folder, which is the original behaviour.
        /// </summary>
        public string LocaleDirectory { get; private set; } = string.Empty;
    }
}
