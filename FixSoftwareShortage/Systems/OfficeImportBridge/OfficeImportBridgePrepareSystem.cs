using System;
using Game;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace FixSoftwareShortage
{
    using ObjectOutsideConnection = Game.Objects.OutsideConnection;

    public partial class OfficeImportBridgePrepareSystem : GameSystemBase
    {
        private const int LogEveryUpdates = 16384;

        private EntityQuery m_BuyerQuery;
        private int m_UpdateCounter;
        private int m_TotalPrepared;
        private int m_LastSummaryPrepared;
        private int m_TotalSkippedPending;
        private int m_TotalSkippedFailed;
        private int m_TotalSkippedNotOutsideConnection;
        private int m_TotalSkippedNoResourcesBuffer;
        private int m_TotalSkippedAlreadyStocked;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_BuyerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ResourceBuyer>(),
                    ComponentType.ReadOnly<PathInformation>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            Mod.LogEssential("[OfficeImportBridge] prepare enabled; mode=vanilla_bridge");
        }

        protected override void OnUpdate()
        {
            m_UpdateCounter++;

            if (OfficeImportBridgeState.Count > 0)
            {
                Mod.LogEssential($"[ERROR][OfficeImportBridge] prepare skipped because cleanup has pending records count={OfficeImportBridgeState.Count}");
                return;
            }

            NativeArray<Entity> entities = m_BuyerQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity request = entities[i];
                    ResourceBuyer buyer = EntityManager.GetComponentData<ResourceBuyer>(request);

                    if (!EconomyUtils.IsOfficeResource(buyer.m_ResourceNeeded) ||
                        (buyer.m_Flags & SetupTargetFlags.Import) == 0)
                    {
                        continue;
                    }

                    PathInformation pathInformation = EntityManager.GetComponentData<PathInformation>(request);
                    if ((pathInformation.m_State & PathFlags.Pending) != 0)
                    {
                        m_TotalSkippedPending++;
                        continue;
                    }

                    if ((pathInformation.m_State & PathFlags.Failed) != 0)
                    {
                        m_TotalSkippedFailed++;
                        Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={buyer.m_ResourceNeeded} request={FormatEntity(request)} destination={FormatEntity(pathInformation.m_Destination)} pathState={pathInformation.m_State} reason=path_failed");
                        continue;
                    }

                    TryPrepare(request, buyer, pathInformation);
                }
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, "[OfficeImportBridge] prepare failed");
            }
            finally
            {
                entities.Dispose();
            }

            LogSummaryIfNeeded(force: m_UpdateCounter == 1);
        }

        private bool TryPrepare(Entity request, ResourceBuyer buyer, PathInformation pathInformation)
        {
            Entity destination = pathInformation.m_Destination;
            if (destination == Entity.Null ||
                !EntityManager.Exists(destination) ||
                !EntityManager.HasComponent<ObjectOutsideConnection>(destination))
            {
                m_TotalSkippedNotOutsideConnection++;
                Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={buyer.m_ResourceNeeded} request={FormatEntity(request)} destination={FormatEntity(destination)} pathState={pathInformation.m_State} reason=destination_not_outside_connection");
                return false;
            }

            if (!EntityManager.HasBuffer<Resources>(destination))
            {
                m_TotalSkippedNoResourcesBuffer++;
                Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={buyer.m_ResourceNeeded} request={FormatEntity(request)} destination={FormatEntity(destination)} pathState={pathInformation.m_State} reason=destination_has_no_resources_buffer");
                return false;
            }

            Resource resource = buyer.m_ResourceNeeded;
            if (OfficeImportBridgeState.Contains(destination, resource))
            {
                return false;
            }

            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(destination);
            int index = FindResourceIndex(resources, resource);
            bool hadEntry = index >= 0;
            int originalAmount = hadEntry ? resources[index].m_Amount : 0;
            if (originalAmount > 0)
            {
                m_TotalSkippedAlreadyStocked++;
                return false;
            }

            const int injectedAmount = 1;
            EconomyUtils.SetResources(resource, resources, injectedAmount);

            OfficeImportBridgeState.Add(request, destination, resource, hadEntry, originalAmount, injectedAmount);
            m_TotalPrepared++;

            Mod.LogDiagnostic($"[OfficeImportBridge] prepared resource={resource} destination={FormatEntity(destination)} request={FormatEntity(request)} payer={FormatEntity(buyer.m_Payer)} originalHadEntry={hadEntry} originalAmount={originalAmount} injectedAmount={injectedAmount} pathState={pathInformation.m_State}");
            return true;
        }

        private void LogSummaryIfNeeded(bool force)
        {
            if (!force && m_UpdateCounter % LogEveryUpdates != 0)
            {
                return;
            }

            if (!force && m_TotalPrepared == m_LastSummaryPrepared)
            {
                return;
            }

            Mod.LogEssential($"[OfficeImportBridge] prepare summary updates={m_UpdateCounter} prepared={m_TotalPrepared} skipped=pending:{m_TotalSkippedPending},failed:{m_TotalSkippedFailed},notOutside:{m_TotalSkippedNotOutsideConnection},noResources:{m_TotalSkippedNoResourcesBuffer},alreadyStocked:{m_TotalSkippedAlreadyStocked}");
            m_LastSummaryPrepared = m_TotalPrepared;
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
