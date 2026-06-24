using System;
using System.Collections.Generic;
using Game;
using Game.Economy;
using Unity.Entities;

namespace FixSoftwareShortage
{
    public partial class OfficeImportBridgeCleanupSystem : GameSystemBase
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.LogEssential("[OfficeImportBridge] cleanup enabled; mode=vanilla_bridge");
        }

        protected override void OnUpdate()
        {
            if (OfficeImportBridgeState.Count == 0)
            {
                return;
            }

            IReadOnlyList<OfficeImportBridgeState.Record> records = OfficeImportBridgeState.Records;
            int restored = 0;

            for (int i = 0; i < records.Count; i++)
            {
                try
                {
                    if (Restore(records[i]))
                    {
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    Mod.LogException(ex, $"[OfficeImportBridge] cleanup failed record={i}");
                }
            }

            if (restored == records.Count)
            {
                Mod.LogDiagnostic($"[OfficeImportBridge] cleanup summary records={records.Count} restored={restored}");
            }
            else
            {
                Mod.LogEssential($"[ERROR][OfficeImportBridge] cleanup incomplete records={records.Count} restored={restored}");
            }

            OfficeImportBridgeState.Clear();
        }

        private bool Restore(OfficeImportBridgeState.Record record)
        {
            if (record.Destination == Entity.Null ||
                !EntityManager.Exists(record.Destination) ||
                !EntityManager.HasBuffer<Resources>(record.Destination))
            {
                Mod.LogEssential($"[ERROR][OfficeImportBridge] cleanup skipped resource={record.Resource} destination={FormatEntity(record.Destination)} request={FormatEntity(record.Request)} reason=destination_missing_or_no_resources");
                return false;
            }

            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(record.Destination);
            int index = FindResourceIndex(resources, record.Resource);

            if (record.HadEntry)
            {
                EconomyUtils.SetResources(record.Resource, resources, record.OriginalAmount);
            }
            else if (index >= 0)
            {
                resources.RemoveAt(index);
            }

            Mod.LogDiagnostic($"[OfficeImportBridge] restored resource={record.Resource} destination={FormatEntity(record.Destination)} request={FormatEntity(record.Request)} originalHadEntry={record.HadEntry} originalAmount={record.OriginalAmount} injectedAmount={record.InjectedAmount}");
            return true;
        }

        private static int FindResourceIndex(DynamicBuffer<Resources> resources, Resource resource)
        {
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i].m_Resource == resource)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string FormatEntity(Entity entity)
        {
            return $"{entity.Index}:{entity.Version}";
        }
    }
}
