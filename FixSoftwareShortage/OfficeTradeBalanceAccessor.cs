using System;
using System.Reflection;
using Game.Economy;
using Game.Simulation;
using HarmonyLib;
using Unity.Collections;

namespace FixSoftwareShortage
{
    internal sealed class OfficeTradeBalanceAccessor
    {
        private readonly TradeSystem m_TradeSystem;
        private readonly FieldInfo m_TradeBalancesField;

        public OfficeTradeBalanceAccessor(TradeSystem tradeSystem)
        {
            m_TradeSystem = tradeSystem;
            m_TradeBalancesField = AccessTools.Field(typeof(TradeSystem), "m_TradeBalances");
        }

        public bool IsAvailable(out string reason)
        {
            if (m_TradeBalancesField == null)
            {
                reason = "TradeSystem.m_TradeBalances field was not found";
                return false;
            }

            if (m_TradeBalancesField.FieldType != typeof(NativeArray<int>))
            {
                reason = $"TradeSystem.m_TradeBalances has unexpected type {m_TradeBalancesField.FieldType.FullName}";
                return false;
            }

            if (!TryGetBalances(out NativeArray<int> balances, out reason))
            {
                return false;
            }

            reason = $"TradeSystem.m_TradeBalances length={balances.Length}";
            return true;
        }

        public bool TryAdd(Resource resource, int delta, out int oldValue, out int newValue, out string reason)
        {
            oldValue = 0;
            newValue = 0;

            if (!TryGetBalances(out NativeArray<int> balances, out reason))
            {
                return false;
            }

            int resourceIndex = EconomyUtils.GetResourceIndex(resource);
            if (resourceIndex < 0 || resourceIndex >= balances.Length)
            {
                reason = $"resourceIndex={resourceIndex} outside trade balance length={balances.Length}";
                return false;
            }

            oldValue = balances[resourceIndex];
            long rawNewValue = (long)oldValue + delta;
            if (rawNewValue > int.MaxValue)
            {
                newValue = int.MaxValue;
            }
            else if (rawNewValue < int.MinValue)
            {
                newValue = int.MinValue;
            }
            else
            {
                newValue = (int)rawNewValue;
            }

            balances[resourceIndex] = newValue;
            reason = "ok";
            return true;
        }

        private bool TryGetBalances(out NativeArray<int> balances, out string reason)
        {
            balances = default;

            try
            {
                if (m_TradeBalancesField == null)
                {
                    reason = "field missing";
                    return false;
                }

                balances = (NativeArray<int>)m_TradeBalancesField.GetValue(m_TradeSystem);
                if (!balances.IsCreated)
                {
                    reason = "NativeArray is not created";
                    return false;
                }

                reason = "ok";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }
}
