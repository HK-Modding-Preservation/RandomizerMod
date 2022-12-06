﻿using RandomizerCore.Logic;
using RandomizerCore.Logic.StateLogic;

namespace RandomizerMod.RC.StateVariables
{
    /*
     * Prefix: $EQUIPPEDCHARM
     * Required Parameters:
     *   - First parameter MUST be either: the name of the charm term (e.g. Gathering_Swarm) or the 1-based charm ID (for Gathering Swarm, 1).
     * Optional Parameters: none
    */
    public class EquipCharmVariable : StateModifyingVariable
    {
        public override string Name { get; }
        public int charmID;
        public Term charmTerm;
        public Term canBenchTerm;
        public Term notchesTerm;
        public StateBool charmBool;
        public StateBool anticharmBool;
        public StateBool overcharmBool;
        public StateBool hasTakenDamage;
        public StateInt usedNotchesInt;

        public const string Prefix = "$EQUIPPEDCHARM";

        protected EquipCharmVariable(string name)
        {
            Name = name;
        }

        public EquipCharmVariable(string name, string charmName, int charmID, LogicManager lm)
        {
            Name = name;
            this.charmID = charmID;
            try
            {
                charmTerm = lm.GetTermStrict(charmName);
                canBenchTerm = lm.GetTermStrict("Can_Bench");
                notchesTerm = lm.GetTermStrict("NOTCHES");
                charmBool = lm.StateManager.GetBoolStrict("CHARM" + charmID);
                anticharmBool = lm.StateManager.GetBoolStrict("noCHARM" + charmID);
                hasTakenDamage = lm.StateManager.GetBoolStrict("HASTAKENDAMAGE");
                overcharmBool = lm.StateManager.GetBoolStrict("OVERCHARMED");
                usedNotchesInt = lm.StateManager.GetIntStrict("USEDNOTCHES");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Error constructing EquipCharmVariable", e);
            }
        }

        public static string GetName(string charmTermName)
        {
            return $"{Prefix}[{charmTermName}]";
        }

        public static string GetName(int charmID)
        {
            return $"{Prefix}[{charmID}]";
        }

        public static bool TryMatch(LogicManager lm, string term, out LogicVariable variable)
        {
            if (VariableResolver.TryMatchPrefix(term, Prefix, out string[] parameters))
            {
                int charmID;
                string charmName;

                if (!int.TryParse(parameters[0], out charmID))
                {
                    charmID = LogicConstUtil.GetCharmID(charmName = parameters[0]);
                }
                else
                {
                    charmName = LogicConstUtil.GetCharmTerm(charmID);
                }

                EquipCharmVariable ecv;
                if (23 <= charmID && charmID <= 25)
                {
                    ecv = new FragileCharmVariable(term, charmName, charmID, lm);
                }
                else 
                {
                    ecv = new EquipCharmVariable(term, charmName, charmID, lm);
                }

                variable = ecv;
                return true;
            }
            variable = default;
            return false;
        }


        public override IEnumerable<Term> GetTerms()
        {
            yield return charmTerm;
            yield return canBenchTerm;
        }

        public override int GetValue(object sender, ProgressionManager pm, StateUnion? localState)
        {
            return CanEquip(pm, localState) switch
            {
                EquipResult.Nonovercharm or EquipResult.Overcharm => TRUE,
                _ => FALSE,
            };
        }

        public enum EquipResult
        {
            None,
            Overcharm,
            Nonovercharm
        }

        public virtual int GetNotchCost<T>(ProgressionManager pm, T state) where T : IState
        {
            return ((RandoModContext)pm.ctx).notchCosts[charmID - 1];
        }

        public virtual bool HasCharmProgression(ProgressionManager pm) => pm.Has(charmTerm) && pm.Has(canBenchTerm);

        /// <summary>
        /// Given that pm.HasCharmProgression returned true, this should determine whether the particular state supports equipping the charm, ignoring notch cost.
        /// </summary>
        protected virtual bool HasStateRequirements<T>(ProgressionManager pm, T state) where T : IState
        {
            if (state.GetBool(anticharmBool)) return false; // cannotBenchBool is also required for midpath charm equips, which aren't covered here.
            return true;
        }

        public bool CanEquipNonovercharm<T>(ProgressionManager pm, T state) where T : IState
        {
            if (HasStateRequirements(pm, state))
            {
                if (state.GetBool(charmBool)) return !state.GetBool(overcharmBool);
                if (state.GetInt(usedNotchesInt) + GetNotchCost(pm, state) <= pm.Get(notchesTerm)) return true;
            }
            return false;
        }

        public bool CanEquipOvercharm<T>(ProgressionManager pm, T state) where T : IState
        {
            if (HasStateRequirements(pm, state))
            {
                if (state.GetBool(charmBool)) return true;
                if (state.GetInt(usedNotchesInt) < pm.Get(notchesTerm)) return true;
            }
            return false;
        }

        public EquipResult CanEquip(ProgressionManager pm, StateUnion? localState)
        {
            if (localState is null || !HasCharmProgression(pm)) return EquipResult.None;
            for (int i = 0; i < localState.Count; i++)
            {
                if (CanEquipNonovercharm(pm, localState[i])) return EquipResult.Nonovercharm;
            }
            for (int i = 0; i < localState.Count; i++)
            {
                if (CanEquipOvercharm(pm, localState[i])) return EquipResult.Overcharm;
            }

            return EquipResult.None;
        }

        /// <summary>
        /// Checks whether the charm can be equipped. Does not modify the state--for that, use <see cref="ModifyState(object, ProgressionManager, ref LazyStateBuilder)"/>.
        /// </summary>
        public EquipResult CanEquip<T>(ProgressionManager pm, T state) where T : IState
        {
            if (!HasCharmProgression(pm) || !HasStateRequirements(pm, state)) return EquipResult.None;
            if (CanEquipNonovercharm(pm, state)) return EquipResult.Nonovercharm;
            if (CanEquipOvercharm(pm, state)) return EquipResult.Overcharm;
            return EquipResult.None;
        }


        public override bool ModifyState(object sender, ProgressionManager pm, ref LazyStateBuilder state)
        {
            if (state.GetBool(charmBool)) return true;

            if (!HasCharmProgression(pm) || !HasStateRequirements(pm, state))
            {
                return false;
            }

            int notchCost = GetNotchCost(pm, state);
            if (notchCost <= 0)
            {
                state.Increment(usedNotchesInt, notchCost);
                state.SetBool(charmBool, true);
                return true;
            }

            int netNotches = pm.Get(notchesTerm) - state.GetInt(usedNotchesInt);
            if (netNotches <= 0)
            {
                return false;
            }
            else 
            {
                if (netNotches < notchCost)
                {
                    if (state.GetBool(hasTakenDamage) || !state.TrySetBoolTrue(overcharmBool))
                    {
                        return false;
                    }
                }
                state.Increment(usedNotchesInt, notchCost);
                state.SetBool(charmBool, true);
                return true;
            }
        }
    }
}