#if DEBUG
using Game;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Objects;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace FixSoftwareShortage
{
    public partial class OfficeExportDiagnosticSystem : GameSystemBase
    {
        private const int LogEveryUpdates = 16384;

        private EntityQuery m_ExporterQuery;
        private EntityQuery m_OutsideConnectionStorageQuery;
        private int m_UpdateCounter;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ExporterQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ResourceExporter>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_OutsideConnectionStorageQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<OutsideConnection>(),
                    ComponentType.ReadOnly<StorageCompany>(),
                    ComponentType.ReadOnly<Resources>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            Mod.LogDiagnostic("[OfficeExportDiagnostic] enabled; vanilla ResourceExporterSystem remains authoritative for office exports");
        }

        protected override void OnUpdate()
        {
            m_UpdateCounter++;
            if (m_UpdateCounter != 1 && m_UpdateCounter % LogEveryUpdates != 0)
            {
                return;
            }

            CountOfficeExporters(out int exporterCount, out int exporterAmount);
            CountOutsideConnectionOfficeStock(out int stockAmount);

            if (m_UpdateCounter == 1 || exporterCount > 0 || stockAmount > 0)
            {
                Mod.LogDiagnostic($"[OfficeExportDiagnostic] summary updates={m_UpdateCounter} officeExporters={exporterCount} officeExporterAmount={exporterAmount} outsideConnectionOfficeStock={stockAmount}");
            }
        }

        private void CountOfficeExporters(out int count, out int amount)
        {
            count = 0;
            amount = 0;

            NativeArray<ResourceExporter> exporters = m_ExporterQuery.ToComponentDataArray<ResourceExporter>(Allocator.Temp);
            try
            {
                for (int i = 0; i < exporters.Length; i++)
                {
                    ResourceExporter exporter = exporters[i];
                    if (!OfficeTradeResources.IsOfficeResource(exporter.m_Resource))
                    {
                        continue;
                    }

                    count++;
                    amount += exporter.m_Amount;
                }
            }
            finally
            {
                exporters.Dispose();
            }
        }

        private void CountOutsideConnectionOfficeStock(out int stockAmount)
        {
            stockAmount = 0;
            NativeArray<Entity> entities = m_OutsideConnectionStorageQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(entity, true);
                    for (int j = 0; j < OfficeTradeResources.Resources.Length; j++)
                    {
                        stockAmount += EconomyUtils.GetResources(OfficeTradeResources.Resources[j], resources);
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
#endif
