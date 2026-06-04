using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;
using UnityEngine;

namespace FixSoftwareShortage
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(FixSoftwareShortage)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{nameof(OnLoad)} version={Application.version}");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            updateSystem.UpdateAfter<OfficeImportFallbackSystem, TradeSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<OfficeExportDiagnosticSystem, TradeSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
