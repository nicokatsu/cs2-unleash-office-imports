using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Game;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Tools;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace FixSoftwareShortage
{
    using ObjectOutsideConnection = Game.Objects.OutsideConnection;

    public partial class OfficeImportBridgePrepareSystem : GameSystemBase
    {
        private const int LogEveryRuns = 1024;

        private EntityQuery m_BuyerQuery;
        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<ResourceBuyer> m_ResourceBuyerType;
        private ComponentTypeHandle<PathInformation> m_PathInformationType;
        private NativeQueue<Candidate> m_Candidates;
        private NativeArray<ChunkStats> m_ChunkStats;
        private int m_RunCounter;
        private long m_TotalScanned;
        private long m_TotalCandidates;
        private long m_TotalPrepared;
        private long m_LastSummaryScanned;
        private long m_LastSummaryCandidates;
        private long m_LastSummaryPrepared;
        private long m_LastSummarySkipped;
        private int m_LastSummaryEmptyRuns;
        private long m_TotalSkippedPending;
        private long m_TotalSkippedFailed;
        private long m_TotalSkippedNotOutsideConnection;
        private long m_TotalSkippedNoResourcesBuffer;
        private long m_TotalSkippedAlreadyStocked;
        private long m_TotalSkippedDuplicate;
        private int m_TotalEmptyRuns;
        private int m_LastActiveChunkCount;
        private int m_LastUnfilteredChunkCount;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return OfficeImportBridgeTiming.GetResourceBuyerAlignedInterval(phase);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_BuyerQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ResourceBuyer>(),
                    ComponentType.ReadOnly<PathInformation>(),
                    ComponentType.ReadOnly<TripNeeded>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            m_EntityType = GetEntityTypeHandle();
            m_ResourceBuyerType = GetComponentTypeHandle<ResourceBuyer>(true);
            m_PathInformationType = GetComponentTypeHandle<PathInformation>(true);
            m_Candidates = new NativeQueue<Candidate>(Allocator.Persistent);

            Mod.LogEssential($"[OfficeImportBridge] prepare enabled; mode=vanilla_bridge interval={OfficeImportBridgeTiming.ResourceBuyerUpdateInterval} offset=inherited_from_ResourceBuyerSystem");
        }

        protected override void OnDestroy()
        {
            if (m_Candidates.IsCreated)
            {
                m_Candidates.Dispose();
            }

            if (m_ChunkStats.IsCreated)
            {
                m_ChunkStats.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            m_RunCounter++;

            if (OfficeImportBridgeState.Count > 0)
            {
                Mod.LogEssential($"[ERROR][OfficeImportBridge] prepare skipped because cleanup has pending records count={OfficeImportBridgeState.Count}");
                return;
            }

            try
            {
                // ResourceBuyerSystem runs at interval 16; changed-version filtering can inject on
                // a path-completion tick and miss the later tick where vanilla actually buys.
                int activeChunkCount = m_BuyerQuery.CalculateChunkCount();
                int unfilteredChunkCount = m_BuyerQuery.CalculateChunkCountWithoutFiltering();
                m_LastActiveChunkCount = activeChunkCount;
                m_LastUnfilteredChunkCount = unfilteredChunkCount;

                if (activeChunkCount == 0)
                {
                    m_TotalEmptyRuns++;
                    LogSummaryIfNeeded(force: m_RunCounter == 1);
                    return;
                }

                EnsureChunkStatsCapacity(unfilteredChunkCount);
                ClearChunkStats(unfilteredChunkCount);
                if (m_Candidates.Count > 0)
                {
                    m_Candidates.Clear();
                }

                m_EntityType.Update(this);
                m_ResourceBuyerType.Update(this);
                m_PathInformationType.Update(this);

                new CollectCandidatesJob
                {
                    EntityType = m_EntityType,
                    ResourceBuyerType = m_ResourceBuyerType,
                    PathInformationType = m_PathInformationType,
                    Candidates = m_Candidates.AsParallelWriter(),
                    Stats = m_ChunkStats
                }.ScheduleParallel(m_BuyerQuery, default(JobHandle)).Complete();

                AccumulateChunkStats(unfilteredChunkCount);
                DrainCandidates();
            }
            catch (Exception ex)
            {
                Mod.LogException(ex, "[OfficeImportBridge] prepare failed");
            }

            LogSummaryIfNeeded(force: m_RunCounter == 1);
        }

        private void DrainCandidates()
        {
            while (m_Candidates.TryDequeue(out Candidate candidate))
            {
                TryPrepare(candidate);
            }
        }

        private bool TryPrepare(Candidate candidate)
        {
            Entity destination = candidate.Destination;
            if (destination == Entity.Null ||
                !EntityManager.Exists(destination) ||
                !EntityManager.HasComponent<ObjectOutsideConnection>(destination))
            {
                m_TotalSkippedNotOutsideConnection++;
                Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={candidate.Resource} request={FormatEntity(candidate.Request)} destination={FormatEntity(destination)} pathState={candidate.PathState} reason=destination_not_outside_connection");
                return false;
            }

            if (!EntityManager.HasBuffer<Resources>(destination))
            {
                m_TotalSkippedNoResourcesBuffer++;
                Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={candidate.Resource} request={FormatEntity(candidate.Request)} destination={FormatEntity(destination)} pathState={candidate.PathState} reason=destination_has_no_resources_buffer");
                return false;
            }

            Resource resource = candidate.Resource;
            if (OfficeImportBridgeState.Contains(destination, resource))
            {
                m_TotalSkippedDuplicate++;
                Mod.LogDiagnostic($"[OfficeImportBridge] skipped resource={resource} request={FormatEntity(candidate.Request)} destination={FormatEntity(destination)} pathState={candidate.PathState} reason=duplicate_bridge_record");
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

            OfficeImportBridgeState.Add(candidate.Request, destination, resource, hadEntry, originalAmount, injectedAmount);
            m_TotalPrepared++;

            Mod.LogDiagnostic($"[OfficeImportBridge] prepared resource={resource} destination={FormatEntity(destination)} request={FormatEntity(candidate.Request)} payer={FormatEntity(candidate.Payer)} originalHadEntry={hadEntry} originalAmount={originalAmount} injectedAmount={injectedAmount} pathState={candidate.PathState}");
            return true;
        }

        private void EnsureChunkStatsCapacity(int chunkCount)
        {
            int capacity = Math.Max(1, chunkCount);
            if (m_ChunkStats.IsCreated && m_ChunkStats.Length >= capacity)
            {
                return;
            }

            if (m_ChunkStats.IsCreated)
            {
                m_ChunkStats.Dispose();
            }

            m_ChunkStats = new NativeArray<ChunkStats>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        private void ClearChunkStats(int chunkCount)
        {
            for (int i = 0; i < chunkCount; i++)
            {
                m_ChunkStats[i] = default;
            }
        }

        private void AccumulateChunkStats(int chunkCount)
        {
            for (int i = 0; i < chunkCount; i++)
            {
                ChunkStats stats = m_ChunkStats[i];
                m_TotalScanned += stats.Scanned;
                m_TotalCandidates += stats.Candidates;
                m_TotalSkippedPending += stats.Pending;
                m_TotalSkippedFailed += stats.Failed;
            }
        }

        private void LogSummaryIfNeeded(bool force)
        {
            if (!force && m_RunCounter % LogEveryRuns != 0)
            {
                return;
            }

            long skipped = GetTotalSkipped();
            if (!force &&
                m_TotalPrepared == m_LastSummaryPrepared &&
                m_TotalScanned == m_LastSummaryScanned &&
                m_TotalCandidates == m_LastSummaryCandidates &&
                skipped == m_LastSummarySkipped &&
                m_TotalEmptyRuns == m_LastSummaryEmptyRuns)
            {
                return;
            }

            Mod.LogEssential($"[OfficeImportBridge] prepare summary runs={m_RunCounter} interval={OfficeImportBridgeTiming.ResourceBuyerUpdateInterval} lastChunks=active:{m_LastActiveChunkCount},unfiltered:{m_LastUnfilteredChunkCount} scanned={m_TotalScanned} candidates={m_TotalCandidates} prepared={m_TotalPrepared} skipped=pending:{m_TotalSkippedPending},failed:{m_TotalSkippedFailed},notOutside:{m_TotalSkippedNotOutsideConnection},noResources:{m_TotalSkippedNoResourcesBuffer},alreadyStocked:{m_TotalSkippedAlreadyStocked},duplicate:{m_TotalSkippedDuplicate},emptyRuns:{m_TotalEmptyRuns}");
            m_LastSummaryScanned = m_TotalScanned;
            m_LastSummaryCandidates = m_TotalCandidates;
            m_LastSummaryPrepared = m_TotalPrepared;
            m_LastSummarySkipped = skipped;
            m_LastSummaryEmptyRuns = m_TotalEmptyRuns;
        }

        private long GetTotalSkipped()
        {
            return m_TotalSkippedPending +
                   m_TotalSkippedFailed +
                   m_TotalSkippedNotOutsideConnection +
                   m_TotalSkippedNoResourcesBuffer +
                   m_TotalSkippedAlreadyStocked +
                   m_TotalSkippedDuplicate;
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

        private struct Candidate
        {
            public Entity Request;
            public Entity Destination;
            public Entity Payer;
            public Resource Resource;
            public PathFlags PathState;
        }

        private struct ChunkStats
        {
            public int Scanned;
            public int Pending;
            public int Failed;
            public int Candidates;
        }

        [BurstCompile]
        private struct CollectCandidatesJob : IJobChunk
        {
            [ReadOnly]
            public EntityTypeHandle EntityType;

            [ReadOnly]
            public ComponentTypeHandle<ResourceBuyer> ResourceBuyerType;

            [ReadOnly]
            public ComponentTypeHandle<PathInformation> PathInformationType;

            public NativeQueue<Candidate>.ParallelWriter Candidates;

            [NativeDisableParallelForRestriction]
            public NativeArray<ChunkStats> Stats;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(EntityType);
                NativeArray<ResourceBuyer> buyers = chunk.GetNativeArray(ref ResourceBuyerType);
                NativeArray<PathInformation> pathInformation = chunk.GetNativeArray(ref PathInformationType);
                ChunkStats stats = default;

                ChunkEntityEnumerator enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out int i))
                {
                    stats.Scanned++;
                    ResourceBuyer buyer = buyers[i];
                    if (!EconomyUtils.IsOfficeResource(buyer.m_ResourceNeeded) ||
                        (buyer.m_Flags & SetupTargetFlags.Import) == 0)
                    {
                        continue;
                    }

                    PathInformation path = pathInformation[i];
                    if ((path.m_State & PathFlags.Pending) != 0)
                    {
                        stats.Pending++;
                        continue;
                    }

                    if ((path.m_State & PathFlags.Failed) != 0)
                    {
                        stats.Failed++;
                        continue;
                    }

                    Candidates.Enqueue(new Candidate
                    {
                        Request = entities[i],
                        Destination = path.m_Destination,
                        Payer = buyer.m_Payer,
                        Resource = buyer.m_ResourceNeeded,
                        PathState = path.m_State
                    });
                    stats.Candidates++;
                }

                if (unfilteredChunkIndex >= 0 && unfilteredChunkIndex < Stats.Length)
                {
                    Stats[unfilteredChunkIndex] = stats;
                }
            }
        }
    }
}
