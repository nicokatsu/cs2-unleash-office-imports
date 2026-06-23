using System.Collections.Generic;
using Game.Economy;
using Unity.Entities;

namespace FixSoftwareShortage
{
    internal static class OfficeImportBridgeState
    {
        private static readonly List<Record> s_Records = new List<Record>();

        public static IReadOnlyList<Record> Records => s_Records;

        public static int Count => s_Records.Count;

        public static void Add(Entity request, Entity destination, Resource resource, bool hadEntry, int originalAmount, int injectedAmount)
        {
            s_Records.Add(new Record(request, destination, resource, hadEntry, originalAmount, injectedAmount));
        }

        public static bool Contains(Entity destination, Resource resource)
        {
            for (int i = 0; i < s_Records.Count; i++)
            {
                Record record = s_Records[i];
                if (record.Destination == destination && record.Resource == resource)
                {
                    return true;
                }
            }

            return false;
        }

        public static void Clear()
        {
            s_Records.Clear();
        }

        public readonly struct Record
        {
            public readonly Entity Destination;
            public readonly Entity Request;
            public readonly Resource Resource;
            public readonly bool HadEntry;
            public readonly int OriginalAmount;
            public readonly int InjectedAmount;

            public Record(Entity request, Entity destination, Resource resource, bool hadEntry, int originalAmount, int injectedAmount)
            {
                Request = request;
                Destination = destination;
                Resource = resource;
                HadEntry = hadEntry;
                OriginalAmount = originalAmount;
                InjectedAmount = injectedAmount;
            }
        }
    }
}
