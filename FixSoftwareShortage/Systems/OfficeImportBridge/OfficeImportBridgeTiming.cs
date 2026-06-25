using Game;

namespace FixSoftwareShortage
{
    internal static class OfficeImportBridgeTiming
    {
        public const int ResourceBuyerUpdateInterval = 16;

        public static int GetResourceBuyerAlignedInterval(SystemUpdatePhase phase)
        {
            return phase == SystemUpdatePhase.GameSimulation ? ResourceBuyerUpdateInterval : 1;
        }
    }
}
