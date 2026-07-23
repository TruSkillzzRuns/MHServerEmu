using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.WebFrontend.Handlers;
using MHServerEmu.WebFrontend.Handlers.MTXStore;
using MHServerEmu.WebFrontend.Handlers.WebApi;
using MHServerEmu.WebFrontend.Network;

namespace MHServerEmu.WebFrontend
{
    /// <summary>
    /// Handles HTTP requests from clients.
    /// </summary>
    public class WebFrontendService : IGameService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly WebFrontendServiceMailbox _serviceMailbox = new();

        private readonly WebService _webService;
        private List<string> _dashboardEndpoints;

        public GameServiceState State { get; private set; } = GameServiceState.Created;

        /// <summary>
        /// Constructs a new <see cref="WebFrontendService"/> instance.
        /// </summary>
        public WebFrontendService()
        {
            var config = ConfigManager.Instance.GetConfig<WebFrontendConfig>();

            WebServiceSettings webServiceSettings = new()
            {
                Name = "WebFrontend",
                ListenUrl = $"http://{config.Address}:{config.Port}/",
                FallbackHandler = new NotFoundWebHandler(),
            };

            _webService = new(webServiceSettings);

            // Register the protobuf handler to the /Login/IndexPB path for compatibility with legacy reverse proxy setups.
            // We should probably prefer to use /AuthServer/Login/IndexPB because it's more accurate to what Gazillion had.
            ProtobufWebHandler protobufHandler = new(config.EnableLoginRateLimit, TimeSpan.FromMilliseconds(config.LoginRateLimitCostMS), config.LoginRateLimitBurst);
            _webService.RegisterHandler("/Login/IndexPB",            protobufHandler);
            _webService.RegisterHandler("/AuthServer/Login/IndexPB", protobufHandler);

            // MTXStore handlers are used for the Add G panel in the client UI.
            _webService.RegisterHandler("/MTXStore/AddG", new AddGWebHandler());
            _webService.RegisterHandler("/MTXStore/AddG/Submit", new AddGSubmitWebHandler());

            if (config.EnableWebApi)
            {
                InitializeWebBackend();
                WebApiKeyManager.Instance.LoadKeys();

                if (config.EnableDashboard)
                    InitializeWebDashboard(config.DashboardFileDirectory, config.DashboardUrlPath);
            }
        }

        #region IGameService Implementation

        /// <summary>
        /// Runs this <see cref="WebFrontendService"/> instance.
        /// </summary>
        public void Run()
        {
            _webService.Start();
            State = GameServiceState.Running;

            while (_webService.IsRunning)
            {
                _serviceMailbox.ProcessMessages();
                Thread.Sleep(1);
            }

            State = GameServiceState.Shutdown;
        }

        /// <summary>
        /// Stops listening and shuts down this <see cref="WebFrontendService"/> instance.
        /// </summary>
        public void Shutdown()
        {
            _webService.Stop();
        }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            _serviceMailbox.PostMessage(message);
        }

        public void GetStatus(Dictionary<string, long> statusDict)
        {
            statusDict["WebFrontendHandlers"] = _webService.HandlerCount;
            statusDict["WebFrontendHandledRequests"] = _webService.HandledRequests;
        }

        #endregion

        public void ReloadDashboard()
        {
            if (_dashboardEndpoints == null)
                return;

            foreach (string localPath in _dashboardEndpoints)
            {
                StaticFileWebHandler fileHandler = _webService.GetHandler(localPath) as StaticFileWebHandler;
                fileHandler?.Load();
            }
        }

        public void ReloadAddGPage()
        {
            AddGWebHandler addGHandler = _webService.GetHandler("/MTXStore/AddG") as AddGWebHandler;
            addGHandler?.Load();
        }

        private void InitializeWebBackend()
        {
            _webService.RegisterHandler("/AccountManagement/Create",        new AccountCreateWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetPlayerName", new AccountSetPlayerNameWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetPassword",   new AccountSetPasswordWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetUserLevel",  new AccountSetUserLevelWebHandler());
            _webService.RegisterHandler("/AccountManagement/SetFlag",       new AccountSetFlagWebHandler());
            _webService.RegisterHandler("/AccountManagement/ClearFlag",     new AccountClearFlagWebHandler());

            _webService.RegisterHandler("/ServerStatus", new ServerStatusWebHandler());

            // PhantomHeroes runtime endpoints.
            _webService.RegisterHandler("/webapi/phantom/spawn",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroSpawnWebHandler());
            _webService.RegisterHandler("/webapi/phantom/clear",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroClearWebHandler());
            _webService.RegisterHandler("/webapi/phantom/status", new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomHeroStatusWebHandler());

            // OmegaDev2 Regions tool endpoints.
            _webService.RegisterHandler("/webapi/regions/list",     new MHServerEmu.WebFrontend.Handlers.WebApi.RegionsListWebHandler());
            _webService.RegisterHandler("/webapi/regions/teleport", new MHServerEmu.WebFrontend.Handlers.WebApi.RegionsTeleportWebHandler());

            // OmegaDev2 Gear Picker.
            _webService.RegisterHandler("/webapi/items/catalog", new MHServerEmu.WebFrontend.Handlers.WebApi.ItemCatalogWebHandler());
            _webService.RegisterHandler("/webapi/items/give",    new MHServerEmu.WebFrontend.Handlers.WebApi.ItemGiveWebHandler());

            // OmegaDev2 Phantom Heroes tool — full command surface over WebAPI.
            _webService.RegisterHandler("/webapi/phantoms/catalog", new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsCatalogWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/status",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsStatusWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/spawn",   new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsSpawnWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/clear",   new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsClearWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/costume", new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsCostumeWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/gear",    new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsGearWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/squads",  new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomsSquadsWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/rogue-encounter", new MHServerEmu.WebFrontend.Handlers.WebApi.RogueEncounterWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/nemesis",         new MHServerEmu.WebFrontend.Handlers.WebApi.NemesisWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/rotation",        new MHServerEmu.WebFrontend.Handlers.WebApi.RotationWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/poweraudit",      new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomPowerAuditWebHandler());
            _webService.RegisterHandler("/webapi/phantoms/poweraudit/damage", new MHServerEmu.WebFrontend.Handlers.WebApi.PhantomPowerDamageAuditWebHandler());

            // OmegaDev2 Enemy Phantoms + Wave Director (Combat pages).
            _webService.RegisterHandler("/webapi/arena/enemyphantoms/spawn",  new MHServerEmu.WebFrontend.Handlers.WebApi.EnemyPhantomsSpawnWebHandler());
            _webService.RegisterHandler("/webapi/arena/enemyphantoms/clear",  new MHServerEmu.WebFrontend.Handlers.WebApi.EnemyPhantomsClearWebHandler());
            _webService.RegisterHandler("/webapi/arena/enemyphantoms/status", new MHServerEmu.WebFrontend.Handlers.WebApi.EnemyPhantomsStatusWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/start",   new MHServerEmu.WebFrontend.Handlers.WebApi.WavesStartWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/stop",    new MHServerEmu.WebFrontend.Handlers.WebApi.WavesStopWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/status",  new MHServerEmu.WebFrontend.Handlers.WebApi.WavesStatusWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/pause",   new MHServerEmu.WebFrontend.Handlers.WebApi.WavesPauseWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/skip",    new MHServerEmu.WebFrontend.Handlers.WebApi.WavesSkipWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/history", new MHServerEmu.WebFrontend.Handlers.WebApi.WavesHistoryWebHandler());
            _webService.RegisterHandler("/webapi/arena/waves/plans",   new MHServerEmu.WebFrontend.Handlers.WebApi.WavePlansWebHandler());
            _webService.RegisterHandler("/webapi/arena/endless/start",   new MHServerEmu.WebFrontend.Handlers.WebApi.EndlessStartWebHandler());
            _webService.RegisterHandler("/webapi/arena/endless/extract", new MHServerEmu.WebFrontend.Handlers.WebApi.EndlessExtractWebHandler());
            _webService.RegisterHandler("/webapi/arena/endless/status",  new MHServerEmu.WebFrontend.Handlers.WebApi.EndlessStatusWebHandler());

            // OmegaDev2 enemy picker (Enemy Phantoms page roster).
            _webService.RegisterHandler("/webapi/enemies/byregion", new MHServerEmu.WebFrontend.Handlers.WebApi.EnemiesByRegionWebHandler());
            _webService.RegisterHandler("/webapi/enemies/catalog",  new MHServerEmu.WebFrontend.Handlers.WebApi.EnemyCatalogWebHandler());

            // OmegaDev2 Stash Manager (inventory read/delete).
            _webService.RegisterHandler("/webapi/inventory",        new MHServerEmu.WebFrontend.Handlers.WebApi.InventoryListWebHandler());
            _webService.RegisterHandler("/webapi/inventory/delete", new MHServerEmu.WebFrontend.Handlers.WebApi.InventoryDeleteWebHandler());

            // OmegaDev2 DPS Meter.
            _webService.RegisterHandler("/webapi/dps",       new MHServerEmu.WebFrontend.Handlers.WebApi.DpsWebHandler());
            _webService.RegisterHandler("/webapi/dps/reset", new MHServerEmu.WebFrontend.Handlers.WebApi.DpsResetWebHandler());

            // OmegaDev2 Leaderboard.
            _webService.RegisterHandler("/webapi/leaderboard",            new MHServerEmu.WebFrontend.Handlers.WebApi.LeaderboardWebHandler());
            _webService.RegisterHandler("/webapi/leaderboard/commit-dps", new MHServerEmu.WebFrontend.Handlers.WebApi.LeaderboardCommitDpsWebHandler());
            _webService.RegisterHandler("/webapi/leaderboard/delete",     new MHServerEmu.WebFrontend.Handlers.WebApi.LeaderboardDeleteWebHandler());
            _webService.RegisterHandler("/webapi/leaderboard/clear",      new MHServerEmu.WebFrontend.Handlers.WebApi.LeaderboardClearWebHandler());

            // OmegaDev2 Account Manager helpers.
            _webService.RegisterHandler("/webapi/playeradmin/warp",  new MHServerEmu.WebFrontend.Handlers.WebApi.PlayerAdminWarpWebHandler());
            _webService.RegisterHandler("/webapi/regionremix/warp",  new MHServerEmu.WebFrontend.Handlers.WebApi.RegionRemixWarpWebHandler());
            _webService.RegisterHandler("/webapi/mods/save",         new MHServerEmu.WebFrontend.Handlers.WebApi.ModSaveWebHandler());

            // OmegaDev2 Command Console + logs.
            _webService.RegisterHandler("/webapi/console/exec", new MHServerEmu.WebFrontend.Handlers.WebApi.ConsoleExecWebHandler());
            _webService.RegisterHandler("/webapi/logs/tail",    new MHServerEmu.WebFrontend.Handlers.WebApi.LogsTailWebHandler());
            _webService.RegisterHandler("/webapi/debug/logs",   new MHServerEmu.WebFrontend.Handlers.WebApi.DebugLogsWebHandler());

            // OmegaDev2 God Mode.
            _webService.RegisterHandler("/webapi/playeradmin/godmode", new MHServerEmu.WebFrontend.Handlers.WebApi.GodModeWebHandler());
            _webService.RegisterHandler("/webapi/playeradmin/godmode/status", new MHServerEmu.WebFrontend.Handlers.WebApi.GodModeStatusWebHandler());

            // OmegaDev2 Currency Editor.
            _webService.RegisterHandler("/webapi/currency/list", new MHServerEmu.WebFrontend.Handlers.WebApi.CurrencyListWebHandler());
            _webService.RegisterHandler("/webapi/currency/set",  new MHServerEmu.WebFrontend.Handlers.WebApi.CurrencySetWebHandler());

            // OmegaDev2 Live Events (real live-tuning event toggle).
            _webService.RegisterHandler("/webapi/livetuning/events",          new MHServerEmu.WebFrontend.Handlers.WebApi.LiveTuningEventsListWebHandler());
            _webService.RegisterHandler("/webapi/livetuning/events/activate", new MHServerEmu.WebFrontend.Handlers.WebApi.LiveTuningEventActivateWebHandler());
            _webService.RegisterHandler("/webapi/livetuning/events/clear",    new MHServerEmu.WebFrontend.Handlers.WebApi.LiveTuningEventClearWebHandler());

            // OmegaDev2 icon/portrait pipeline. Reads textures from the
            // USER'S OWN client install ([ClientAssets] in Config.ini —
            // empty by default = endpoints return 404 and the tool shows
            // no pictures). No game assets ship with the server.
            _webService.RegisterHandler("/webapi/portrait",  new MHServerEmu.WebFrontend.Handlers.WebApi.PortraitWebHandler());
            _webService.RegisterHandler("/webapi/texbyname", new MHServerEmu.WebFrontend.Handlers.WebApi.TextureByNameWebHandler());

            // OmegaDev2 Prototype Field Editor (runtime, MetaGame/MetaState) — see RuntimePrototypeEditor for the mutation model.
            _webService.RegisterHandler("/webapi/protoeditor/discover", new MHServerEmu.WebFrontend.Handlers.WebApi.PrototypeEditorDiscoverWebHandler());
            _webService.RegisterHandler("/webapi/protoeditor/fields",   new MHServerEmu.WebFrontend.Handlers.WebApi.PrototypeEditorReadWebHandler());
            _webService.RegisterHandler("/webapi/protoeditor/write",    new MHServerEmu.WebFrontend.Handlers.WebApi.PrototypeEditorWriteWebHandler());
            _webService.RegisterHandler("/webapi/protoeditor/clone",    new MHServerEmu.WebFrontend.Handlers.WebApi.PrototypeEditorCloneWebHandler());

            _webService.RegisterHandler("/RegionReport", new RegionReportWebHandler());
            _webService.RegisterHandler("/Metrics/Performance", new MetricsPerformanceWebHandler());
        }

        private void InitializeWebDashboard(string dashboardDirectoryName, string localPath)
        {
            string dashboardDirectory = Path.Combine(FileHelper.DataDirectory, "Web", dashboardDirectoryName);
            if (Directory.Exists(dashboardDirectory) == false)
            {
                Logger.Warn($"InitializeWebDashboard(): Dashboard directory '{dashboardDirectoryName}' does not exist");
                return;
            }

            string indexFilePath = Path.Combine(dashboardDirectory, "index.html");
            if (File.Exists(indexFilePath) == false)
            {
                Logger.Warn($"InitializeWebDashboard(): Index file not found at '{indexFilePath}'");
                return;
            }

            _dashboardEndpoints = new();

            // Make sure local path starts and ends with slashes.
            if (localPath.StartsWith('/') == false)
                localPath = $"/{localPath}";

            if (localPath.EndsWith('/') == false)
                localPath = $"{localPath}/";

            _webService.RegisterHandler(localPath, new StaticFileWebHandler(indexFilePath));
            _dashboardEndpoints.Add(localPath);

            // Add redirect for requests to our dashboard "directory" that don't have trailing slashes.
            if (localPath.Length > 1)
            {
                string localPathRedirect = localPath[..^1];
                _webService.RegisterHandler(localPathRedirect, new TrailingSlashRedirectWebHandler());
                _dashboardEndpoints.Add(localPathRedirect);
            }

            // Register other files.
            foreach (string filePath in Directory.GetFiles(dashboardDirectory, "*", SearchOption.AllDirectories))
            {
                string relativeFilePath = Path.GetRelativePath(dashboardDirectory, filePath);

                if (string.Equals(relativeFilePath, "index.html", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                string subFilePath = $"{localPath}{relativeFilePath.Replace('\\', '/')}";

                _webService.RegisterHandler(subFilePath, new StaticFileWebHandler(filePath));
                _dashboardEndpoints.Add(subFilePath);
            }

            Logger.Info($"Initialized web dashboard at {localPath}");
        }
    }
}
