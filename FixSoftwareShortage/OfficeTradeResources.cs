using Game.Economy;

namespace FixSoftwareShortage
{
    internal static class OfficeTradeResources
    {
        public static readonly Resource[] Resources =
        {
            Resource.Software,
            Resource.Telecom,
            Resource.Financial,
            Resource.Media
        };

        public static bool IsOfficeResource(Resource resource)
        {
            return resource == Resource.Software ||
                   resource == Resource.Telecom ||
                   resource == Resource.Financial ||
                   resource == Resource.Media;
        }
    }
}
