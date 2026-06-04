using System;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace FixSoftwareShortage
{
    using ObjectOutsideConnection = Game.Objects.OutsideConnection;

    public partial class OfficeImportFallbackSystem : GameSystemBase
    {
        private const int LogEveryUpdates = 16384;

        private EntityQuery m_BuyerQuery;
        private EntityQuery m_OutsideConnectionQuery;
        private ResourceSystem m_ResourceSystem;
        private TradeSystem m_TradeSystem;
        private CitySystem m_CitySystem;
        private CityStatisticsSystem m_CityStatisticsSystem;
        private CityProductionStatisticSystem m_CityProductionStatisticSystem;
        private OfficeTradeBalanceAccessor m_TradeBalanceAccessor;

        private bool m_FallbackEnabled;
        private bool m_DisabledLogged;
        private int m_UpdateCounter;
        private int m_TotalFallbackImports;
        private int m_TotalImportedAmount;
        private int m_TotalSkippedPending;
        private int m_TotalSkippedVanillaTarget;
        private int m_TotalSkippedNoOutsideConnection;
        private int m_TotalSkippedInvalidPrice;
        private int m_TotalSkippedInvalidPayer;
        private readonly int[] m_ImportedByResource = new int[OfficeTradeResources.Resources.Length];

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_TradeSystem = World.GetOrCreateSystemManaged<TradeSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_CityStatisticsSystem = World.GetOrCreateSystemManaged<CityStatisticsSystem>();
            m_CityProductionStatisticSystem = World.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            m_TradeBalanceAccessor = new OfficeTradeBalanceAccessor(m_TradeSystem);

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

            m_OutsideConnectionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ObjectOutsideConnection>(),
                    ComponentType.ReadOnly<PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });

            if (ValidateDependencies(out string validation))
            {
                m_FallbackEnabled = true;
                Mod.log.Info($"[OfficeImportFallback] enabled; {validation}");
            }
            else
            {
                m_FallbackEnabled = false;
                Mod.log.Info($"[ERROR][OfficeImportFallback] disabled; {validation}");
            }
        }

        protected override void OnUpdate()
        {
            m_UpdateCounter++;

            if (!m_FallbackEnabled)
            {
                if (!m_DisabledLogged)
                {
                    Mod.log.Info("[ERROR][OfficeImportFallback] skipped update because fallback is disabled");
                    m_DisabledLogged = true;
                }

                return;
            }

            OutsideConnectionTransferType outsideConnectionTypes = GetOutsideConnectionTypes();
            if (outsideConnectionTypes == OutsideConnectionTransferType.None)
            {
                m_TotalSkippedNoOutsideConnection += m_BuyerQuery.CalculateEntityCount();
                LogSummaryIfNeeded(outsideConnectionTypes, force: m_UpdateCounter == 1);
                return;
            }

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.Exists(city) || !EntityManager.HasBuffer<CityModifier>(city))
            {
                m_TotalSkippedInvalidPrice += m_BuyerQuery.CalculateEntityCount();
                LogSummaryIfNeeded(outsideConnectionTypes, force: m_UpdateCounter == 1);
                return;
            }

            DynamicBuffer<CityModifier> cityEffects = EntityManager.GetBuffer<CityModifier>(city, true);
            NativeArray<Entity> entities = m_BuyerQuery.ToEntityArray(Allocator.Temp);
            ResourcePrefabs prefabs = m_ResourceSystem.GetPrefabs();
            ComponentLookup<ResourceData> resourceDatas = GetComponentLookup<ResourceData>(true);
            int importsBeforeUpdate = m_TotalFallbackImports;

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    ResourceBuyer buyer = EntityManager.GetComponentData<ResourceBuyer>(entity);

                    if (!OfficeTradeResources.IsOfficeResource(buyer.m_ResourceNeeded) ||
                        (buyer.m_Flags & SetupTargetFlags.Import) == 0)
                    {
                        continue;
                    }

                    PathInformation pathInformation = EntityManager.GetComponentData<PathInformation>(entity);
                    if ((pathInformation.m_State & PathFlags.Pending) != 0)
                    {
                        m_TotalSkippedPending++;
                        continue;
                    }

                    if (!ShouldFallback(pathInformation, buyer.m_ResourceNeeded, out string targetReason))
                    {
                        m_TotalSkippedVanillaTarget++;
                        continue;
                    }

                    TryExecuteFallbackImport(entity, buyer, pathInformation, targetReason, outsideConnectionTypes, cityEffects, prefabs, ref resourceDatas);
                }
            }
            finally
            {
                entities.Dispose();
            }

            LogSummaryIfNeeded(outsideConnectionTypes, force: m_TotalFallbackImports > importsBeforeUpdate);
        }

        private bool ValidateDependencies(out string validation)
        {
            if (!m_TradeBalanceAccessor.IsAvailable(out string balanceReason))
            {
                validation = balanceReason;
                return false;
            }

            try
            {
                JobHandle deps;
                m_CityProductionStatisticSystem.GetCityResourceUsageAccumulator(
                    CityProductionStatisticSystem.CityResourceUsage.Consumer.ImportExport,
                    out deps);
            }
            catch (Exception ex)
            {
                validation = $"CityProductionStatisticSystem accumulator unavailable: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            validation = balanceReason;
            return true;
        }

        private bool TryExecuteFallbackImport(
            Entity requestEntity,
            ResourceBuyer buyer,
            PathInformation pathInformation,
            string targetReason,
            OutsideConnectionTransferType outsideConnectionTypes,
            DynamicBuffer<CityModifier> cityEffects,
            ResourcePrefabs prefabs,
            ref ComponentLookup<ResourceData> resourceDatas)
        {
            Resource resource = buyer.m_ResourceNeeded;
            int amount = buyer.m_AmountNeeded;
            if (amount <= 0)
            {
                m_TotalSkippedInvalidPrice++;
                Mod.log.Info($"[OfficeImportFallback] skipped resource={resource} entity={requestEntity.Index}:{requestEntity.Version} amount={amount} reason=invalid_amount");
                return false;
            }

            Entity payer = buyer.m_Payer;
            if (payer == Entity.Null || !EntityManager.Exists(payer) || !EntityManager.HasBuffer<Resources>(payer))
            {
                m_TotalSkippedInvalidPayer++;
                Mod.log.Info($"[OfficeImportFallback] skipped resource={resource} entity={requestEntity.Index}:{requestEntity.Version} payer={payer.Index}:{payer.Version} reason=invalid_payer");
                return false;
            }

            float industrialPrice = EconomyUtils.GetIndustrialPrice(resource, prefabs, ref resourceDatas);
            float importTradePrice = m_TradeSystem.GetBestTradePriceAmongTypes(resource, outsideConnectionTypes, import: true, cityEffects);
            if (!IsUsablePrice(industrialPrice) || !IsUsablePrice(importTradePrice))
            {
                m_TotalSkippedInvalidPrice++;
                Mod.log.Info($"[OfficeImportFallback] skipped resource={resource} entity={requestEntity.Index}:{requestEntity.Version} industrialPrice={industrialPrice} importTradePrice={importTradePrice} outsideTypes={outsideConnectionTypes} reason=invalid_vanilla_price");
                return false;
            }

            float unitPrice = industrialPrice + importTradePrice;
            if (!IsUsablePrice(unitPrice))
            {
                m_TotalSkippedInvalidPrice++;
                Mod.log.Info($"[OfficeImportFallback] skipped resource={resource} entity={requestEntity.Index}:{requestEntity.Version} unitPrice={unitPrice} reason=invalid_unit_price");
                return false;
            }

            int clampedAmount = ClampAmountForMoneyDelta(amount, unitPrice);
            if (clampedAmount <= 0)
            {
                m_TotalSkippedInvalidPrice++;
                Mod.log.Info($"[OfficeImportFallback] skipped resource={resource} entity={requestEntity.Index}:{requestEntity.Version} amount={amount} unitPrice={unitPrice} reason=price_overflow_guard");
                return false;
            }

            int totalPrice = UnityEngine.Mathf.RoundToInt(unitPrice * clampedAmount);
            DynamicBuffer<Resources> payerResources = EntityManager.GetBuffer<Resources>(payer);
            EconomyUtils.AddResources(Resource.Money, -math.abs(totalPrice), payerResources);
            EconomyUtils.AddResources(resource, clampedAmount, payerResources);

            if (EntityManager.HasComponent<BuyingCompany>(payer))
            {
                BuyingCompany buyingCompany = EntityManager.GetComponentData<BuyingCompany>(payer);
                buyingCompany.m_LastTradePartner = Entity.Null;
                EntityManager.SetComponentData(payer, buyingCompany);
            }

            if (EntityManager.HasComponent<CompanyStatisticData>(payer))
            {
                CompanyStatisticData statisticData = EntityManager.GetComponentData<CompanyStatisticData>(payer);
                statisticData.m_CurrentCostOfBuyingResources = AddClamped(statisticData.m_CurrentCostOfBuyingResources, math.abs(totalPrice));
                EntityManager.SetComponentData(payer, statisticData);
            }

            if (!RecordTradeBalance(resource, clampedAmount, requestEntity))
            {
                RollbackResources(payerResources, resource, clampedAmount, totalPrice);
                return false;
            }

            RecordImportStatistics(resource, clampedAmount);
            CompleteResourceBuyer(requestEntity);

            m_TotalFallbackImports++;
            m_TotalImportedAmount += clampedAmount;
            int resourceSlot = GetResourceSlot(resource);
            if (resourceSlot >= 0)
            {
                m_ImportedByResource[resourceSlot] += clampedAmount;
            }

            Mod.log.Info($"[OfficeImportFallback] imported resource={resource} amount={clampedAmount} price={totalPrice} unitPrice={unitPrice:0.###} industrialPrice={industrialPrice:0.###} importTradePrice={importTradePrice:0.###} entity={requestEntity.Index}:{requestEntity.Version} payer={payer.Index}:{payer.Version} pathState={pathInformation.m_State} targetReason={targetReason}");
            return true;
        }

        private bool ShouldFallback(PathInformation pathInformation, Resource resource, out string reason)
        {
            if ((pathInformation.m_State & PathFlags.Failed) != 0)
            {
                reason = "path_failed";
                return true;
            }

            Entity destination = pathInformation.m_Destination;
            if (destination == Entity.Null)
            {
                reason = "destination_null";
                return true;
            }

            if (!EntityManager.Exists(destination))
            {
                reason = "destination_missing";
                return true;
            }

            bool vanillaTradeTarget = EntityManager.HasComponent<PropertyRenter>(destination) ||
                                      EntityManager.HasComponent<ObjectOutsideConnection>(destination);
            if (!vanillaTradeTarget)
            {
                reason = "destination_not_trade_target";
                return true;
            }

            if (!EntityManager.HasBuffer<Resources>(destination))
            {
                reason = "destination_has_no_resources";
                return true;
            }

            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(destination, true);
            int storedAmount = EconomyUtils.GetResources(resource, resources);
            if (storedAmount <= 0)
            {
                reason = "destination_cannot_supply";
                return true;
            }

            reason = "vanilla_target_available";
            return false;
        }

        private OutsideConnectionTransferType GetOutsideConnectionTypes()
        {
            OutsideConnectionTransferType result = OutsideConnectionTransferType.None;
            NativeArray<Entity> entities = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);
                    if (!EntityManager.HasComponent<OutsideConnectionData>(prefabRef.m_Prefab))
                    {
                        continue;
                    }

                    OutsideConnectionData data = EntityManager.GetComponentData<OutsideConnectionData>(prefabRef.m_Prefab);
                    result |= data.m_Type & OutsideConnectionTransferType.All;
                }
            }
            finally
            {
                entities.Dispose();
            }

            return result;
        }

        private bool RecordTradeBalance(Resource resource, int amount, Entity requestEntity)
        {
            if (m_TradeBalanceAccessor.TryAdd(resource, -amount, out int oldValue, out int newValue, out string reason))
            {
                return true;
            }

            m_TotalSkippedInvalidPrice++;
            Mod.log.Info($"[ERROR][OfficeImportFallback] could not update trade balance resource={resource} amount={amount} entity={requestEntity.Index}:{requestEntity.Version} reason={reason}; transaction rolled back");
            return false;
        }

        private void RecordImportStatistics(Resource resource, int amount)
        {
            int resourceIndex = EconomyUtils.GetResourceIndex(resource);

            JobHandle productionDeps;
            NativeArray<int> importExportAccumulator = m_CityProductionStatisticSystem.GetCityResourceUsageAccumulator(
                CityProductionStatisticSystem.CityResourceUsage.Consumer.ImportExport,
                out productionDeps);
            productionDeps.Complete();
            importExportAccumulator[resourceIndex] = AddClamped(importExportAccumulator[resourceIndex], -amount);
            m_CityProductionStatisticSystem.AddCityUsageAccumulatorWriter(
                CityProductionStatisticSystem.CityResourceUsage.Consumer.ImportExport,
                default(JobHandle));

            if (m_CityStatisticsSystem.Enabled)
            {
                JobHandle statisticsDeps;
                NativeQueue<StatisticsEvent> statisticsQueue = m_CityStatisticsSystem.GetStatisticsEventQueue(out statisticsDeps);
                statisticsDeps.Complete();
                statisticsQueue.Enqueue(new StatisticsEvent
                {
                    m_Statistic = StatisticType.Trade,
                    m_Parameter = resourceIndex,
                    m_Change = amount
                });
                m_CityStatisticsSystem.AddWriter(default(JobHandle));
            }
        }

        private void CompleteResourceBuyer(Entity entity)
        {
            if (EntityManager.HasComponent<ResourceBuyer>(entity))
            {
                EntityManager.RemoveComponent<ResourceBuyer>(entity);
            }

            if (EntityManager.HasComponent<PathInformation>(entity))
            {
                EntityManager.RemoveComponent<PathInformation>(entity);
            }

            if (EntityManager.HasBuffer<PathElement>(entity))
            {
                EntityManager.RemoveComponent<PathElement>(entity);
            }
        }

        private static void RollbackResources(DynamicBuffer<Resources> resources, Resource resource, int amount, int totalPrice)
        {
            EconomyUtils.AddResources(resource, -amount, resources);
            EconomyUtils.AddResources(Resource.Money, math.abs(totalPrice), resources);
        }

        private static bool IsUsablePrice(float price)
        {
            return !float.IsNaN(price) &&
                   !float.IsInfinity(price) &&
                   price != float.MaxValue &&
                   price != float.MinValue &&
                   price >= 0f;
        }

        private static int ClampAmountForMoneyDelta(int amount, float unitPrice)
        {
            if (unitPrice <= 0f)
            {
                return amount;
            }

            float maxAmount = (int.MaxValue - 1024) / unitPrice;
            if (maxAmount <= 0f)
            {
                return 0;
            }

            return math.min(amount, UnityEngine.Mathf.FloorToInt(maxAmount));
        }

        private static int AddClamped(int value, int delta)
        {
            long rawValue = (long)value + delta;
            if (rawValue > int.MaxValue)
            {
                return int.MaxValue;
            }

            if (rawValue < int.MinValue)
            {
                return int.MinValue;
            }

            return (int)rawValue;
        }

        private static int GetResourceSlot(Resource resource)
        {
            for (int i = 0; i < OfficeTradeResources.Resources.Length; i++)
            {
                if (OfficeTradeResources.Resources[i] == resource)
                {
                    return i;
                }
            }

            return -1;
        }

        private void LogSummaryIfNeeded(OutsideConnectionTransferType outsideConnectionTypes, bool force)
        {
            if (!force && m_UpdateCounter % LogEveryUpdates != 0)
            {
                return;
            }

            Mod.log.Info($"[OfficeImportFallback] summary updates={m_UpdateCounter} outsideTypes={outsideConnectionTypes} trades={m_TotalFallbackImports} amount={m_TotalImportedAmount} byResource=Software:{m_ImportedByResource[0]},Telecom:{m_ImportedByResource[1]},Financial:{m_ImportedByResource[2]},Media:{m_ImportedByResource[3]} skips=pending:{m_TotalSkippedPending},vanillaTarget:{m_TotalSkippedVanillaTarget},noOutside:{m_TotalSkippedNoOutsideConnection},invalidPrice:{m_TotalSkippedInvalidPrice},invalidPayer:{m_TotalSkippedInvalidPayer}");
        }
    }
}
