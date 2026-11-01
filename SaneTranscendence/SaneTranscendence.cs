using System;
using BepInEx;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2;
using UnityEngine;
using R2API;

namespace SaneTranscendence;

[BepInDependency(LanguageAPI.PluginGUID)]

[BepInPlugin("com.paimcakes.sanetranscendence", "SaneTranscendence", "1.0.0")]
public class SaneTranscendence : BaseUnityPlugin
{
    private const float DelayPerStack = 0.2f;       // +20% delay per stack
    private const float RechargeKeptPerStack = 0.8f; // x0.8 recharge rate per stack

    private void Awake()
    {
        IL.RoR2.CharacterBody.RecalculateStats += ChangeItemStats;
        IL.RoR2.CharacterBody.UpdateOutOfCombatAndDanger += PatchDangerDelay;
        IL.RoR2.HealthComponent.ServerFixedUpdate += PatchShieldRecharge;
        RoR2Application.onLoad += UpdateDescription;
    }

    private static int Stacks(CharacterBody body)
    {
        if (!body || !body.inventory) return 0;
        return body.inventory.GetItemCountEffective(RoR2Content.Items.ShieldOnly);
    }

    private void ChangeItemStats(ILContext il)
    {
        var c = new ILCursor(il);
        if (c.TryGotoNext(MoveType.Before,
                x => x.MatchLdcR4(1.5f),
                x => x.MatchLdloc(out _),   // num15
                x => x.MatchLdcI4(1),
                x => x.MatchSub()))
        {
            c.Index++;               // step past the 1.5f
            c.Emit(OpCodes.Ldarg_0); // "this" (the CharacterBody)
            c.EmitDelegate<Func<float, CharacterBody, float>>((vanilla, body) =>
                Stacks(body) > 0 ? 1.0f : vanilla);
        }
        else Logger.LogError("SaneTranscendence: health IL match failed");
    }
    private void PatchDangerDelay(ILContext il)
    {
        var c = new ILCursor(il);
        if (c.TryGotoNext(MoveType.After,
                x => x.MatchLdfld<CharacterBody>("outOfDangerStopwatch")
                     || x.MatchCallvirt<CharacterBody>("get_outOfDangerStopwatch"),
                x => x.MatchLdcR4(7f)))
        {
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<float, CharacterBody, float>>((delay, body) =>
                delay * (1f + DelayPerStack * Stacks(body)));
        }
        else
        {
            Logger.LogError($"SaneTranscendence: delay match failed in {il.Method.Name}. Instructions touching outOfDanger or 7f:");
            var list = il.Instrs;
            for (int i = 0; i < list.Count; i++)
            {
                bool hit = (list[i].Operand is MemberReference m && m.Name.Contains("outOfDanger"))
                           || (list[i].Operand is float f && f == 7f);
                if (!hit) continue;

                Logger.LogError($"--- at instruction {i}");
                for (int j = Math.Max(0, i - 1); j <= Math.Min(list.Count - 1, i + 3); j++)
                    Logger.LogError("    " + Describe(list[j]));
            }
        }
    }

    private static string Describe(Instruction ins)
    {
        string operand;
        if (ins.Operand == null) operand = "";
        else if (ins.Operand is MemberReference m) operand = m.FullName;
        else if (ins.Operand is float || ins.Operand is int) operand = ins.Operand.ToString();
        else operand = ins.Operand.GetType().Name; // branch targets, variables, etc.
        return ins.OpCode + " " + operand;
    }

    private void PatchShieldRecharge(ILContext il)
    {
        var c = new ILCursor(il);
        // find the shield-end sound, then step back to the 0.5f just before it
        if (c.TryGotoNext(x => x.MatchLdstr("Play_item_proc_personal_shield_end"))
            && c.TryGotoPrev(MoveType.After, x => x.MatchLdcR4(0.5f)))
        {
            c.Emit(OpCodes.Ldarg_0); // "this" (the HealthComponent)
            c.EmitDelegate<Func<float, HealthComponent, float>>((rate, hc) =>
                rate * Mathf.Pow(RechargeKeptPerStack, Stacks(hc.body)));
        }
        else Logger.LogError("SaneTranscendence: shield recharge IL match failed");
    }
    
    private void UpdateDescription()
    {
        ItemDef item = RoR2Content.Items.ShieldOnly;

        string desc =
            "<style=cIsHealing>Convert all but 1 health</style> into <style=cIsHealing>regenerating shields</style>. " +
            "Gain <style=cIsHealing>0% maximum health</style> <style=cStack>(+25% per stack)</style>. " +
            $"Increase shield recharge delay by <style=cIsDamage>{DelayPerStack * 100:0}%</style> <style=cStack>(+{DelayPerStack * 100:0}% per stack)</style>. " +
            $"Reduce shield recharge rate by <style=cIsDamage>{(1f - RechargeKeptPerStack) * 100:0}%</style> <style=cStack>(-{(1f - RechargeKeptPerStack) * 100:0}% per stack)</style>.";

        LanguageAPI.Add(item.descriptionToken, desc);
        LanguageAPI.Add(item.pickupToken, "Convert all but 1 health into regenerating shields. Slower shield recharge.");
    }
}

