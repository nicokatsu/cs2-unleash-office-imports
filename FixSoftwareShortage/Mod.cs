using Colossal.Logging;
using Game;
using System;
using System.Diagnostics;
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
            string gameVersion = Application.version;
            LogEssential($"{nameof(OnLoad)} version={gameVersion} mode=vanilla_bridge");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                LogEssential($"Current mod asset at {asset.path}");
            }

            updateSystem.UpdateBefore<OfficeImportBridgePrepareSystem, ResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<OfficeImportBridgeCleanupSystem, ResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
            LogEssential("[OfficeImportBridge] registered; prepare=before ResourceBuyerSystem cleanup=after ResourceBuyerSystem");

#if DEBUG
            updateSystem.UpdateAfter<OfficeExportDiagnosticSystem, TradeSystem>(SystemUpdatePhase.GameSimulation);
#endif
        }

        public void OnDispose()
        {
            LogEssential(nameof(OnDispose));
        }

        internal static void LogEssential(string message)
        {
            log.Info(message);
        }

        [Conditional("DEBUG")]
        internal static void LogDiagnostic(string message)
        {
            log.Info(message);
        }

        internal static void LogException(Exception exception, string message)
        {
            log.Info(exception, $"[ERROR] {message}");
        }
    }
}
