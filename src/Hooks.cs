using ImprovedInput;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RWCustom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;
using static MonoMod.InlineRT.MonoModRule;
using static Unity.IO.LowLevel.Unsafe.AsyncReadManagerMetrics;
using Random = UnityEngine.Random;

namespace ArtificerDontSwallow
{
    internal static class Hooks
    {
        public static void ApplyHooks()
        {
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;

            On.Player.SwallowObject += Player_SwallowObject;

            On.Player.CraftingResults += Player_CraftingResults;
        }


        private static bool isInit = false;

        private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            if (isInit) return;
            isInit = true;

            MachineConnector.SetRegisteredOI(Plugin.MOD_ID, Options.instance);

            try
            {
                IL.Player.GrabUpdate += Player_GrabUpdateIL;
                IL.Player.SwallowObject += Player_SwallowObjectIL;

                IL.Player.SpitUpCraftedObject += Player_SpitUpCraftedObject;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(ex);
            }
        }


        private static AbstractPhysicalObject.AbstractObjectType Player_CraftingResults(On.Player.orig_CraftingResults orig, Player self)
        {
            AbstractPhysicalObject.AbstractObjectType result = orig(self);

            if (result != null) return result;
            
            for (int i = 0; i < 2; i++)
            {
                if (self.grasps[i] == null) continue;

                AbstractPhysicalObject.AbstractObjectType type = self.grasps[i].grabbed.abstractPhysicalObject.type;


                if (type == AbstractPhysicalObject.AbstractObjectType.Rock)
                    return AbstractPhysicalObject.AbstractObjectType.ScavengerBomb;

                if (type == AbstractPhysicalObject.AbstractObjectType.FlyLure)
                    return AbstractPhysicalObject.AbstractObjectType.FirecrackerPlant;

                if (type == AbstractPhysicalObject.AbstractObjectType.FlareBomb)
                    return AbstractPhysicalObject.AbstractObjectType.Lantern;
            }

            return null!;
        }

        private static void Player_SpitUpCraftedObject(ILContext il)
        {
            ILCursor c = new ILCursor(il);


            c.GotoNext(MoveType.After,
                x => x.MatchLdnull(),
                x => x.MatchStloc(0));

            ILLabel? afterHook = c.MarkLabel();

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Player, bool>>((self) =>
            {
                for (int i = 0; i < self.grasps.Length; i++)
                {
                    if (self.grasps[i] == null) continue;

                    AbstractPhysicalObject heldObject = self.grasps[i].grabbed.abstractPhysicalObject;

                    if (!IsObjectCraftable(heldObject)) continue;

                    AbstractPhysicalObject? abstractCraftedObject = GetCraftedObject(self, heldObject);
                    if (abstractCraftedObject == null) continue;

                    self.ReleaseGrasp(i);
                    heldObject.realizedObject.RemoveFromRoom();
                    self.room.abstractRoom.RemoveEntity(heldObject);
                    self.SubtractFood(1);

                    self.room.abstractRoom.AddEntity(abstractCraftedObject);
                    abstractCraftedObject.RealizeInRoom();

                    if (self.FreeHand() != -1)
                        self.SlugcatGrab(abstractCraftedObject.realizedObject, self.FreeHand());

                    return false;
                }

                return true;
            });

            c.Emit(OpCodes.Brtrue, afterHook);
            c.Emit(OpCodes.Ret);
            c.MarkLabel(afterHook);
        }

        private static AbstractPhysicalObject? GetCraftedObject(Player self, AbstractPhysicalObject ingredientObject)
        {
            if (ingredientObject.type == AbstractPhysicalObject.AbstractObjectType.Rock)
                return new AbstractPhysicalObject(self.room.world, AbstractPhysicalObject.AbstractObjectType.ScavengerBomb, null, self.room.GetWorldCoordinate(self.mainBodyChunk.pos), self.room.game.GetNewID());

            else if (ingredientObject.type == AbstractPhysicalObject.AbstractObjectType.FlyLure)
                return new AbstractConsumable(self.room.world, AbstractPhysicalObject.AbstractObjectType.FirecrackerPlant, null, self.room.GetWorldCoordinate(self.mainBodyChunk.pos), self.room.game.GetNewID(), -1, -1, null);

            else if (ingredientObject.type == AbstractPhysicalObject.AbstractObjectType.FlareBomb)
                return new AbstractPhysicalObject(self.room.world, AbstractPhysicalObject.AbstractObjectType.Lantern, null, self.room.GetWorldCoordinate(self.mainBodyChunk.pos), self.room.game.GetNewID());

            return null;
        }



        private static void Player_SwallowObjectIL(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel? afterCraft = null;

            c.GotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt<Player>("get_FoodInStomach"),
                x => x.MatchLdcI4(0),
                x => x.MatchBle(out afterCraft));

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Player, bool>>((self) =>
            {
                if (!Options.disableForceCraft.Value) return true;
                
                if (!PlayersCrafting.TryGetValue(self, out var isCrafting)) return true;

                return isCrafting.Value;
            });

            c.Emit(OpCodes.Brfalse, afterCraft);
        }

        private static void Player_GrabUpdateIL(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            // Normally cannot swallow when Y is held, allow it when a held object is craftable
            c.GotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt<Player>("SpitUpCraftedObject"),
                x => x.MatchLdarg(0),
                x => x.MatchLdcI4(0));

            c.GotoNext(MoveType.After,
                x => x.Match(OpCodes.Ldelema),
                x => x.MatchLdfld<Player.InputPackage>("y"));

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Player, bool>>((self) =>
            {
                if (self.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                    return true;

                for (int i = 0; i < 2; i++)
                {
                    if (self.grasps[i] == null) continue;

                    if (IsObjectCraftable(self.grasps[i].grabbed.abstractPhysicalObject))
                        return false;
                }

                return true;
            });
            
            c.Emit(OpCodes.And);
        }

        private static ConditionalWeakTable<Player, StrongBox<bool>> PlayersCrafting = new ConditionalWeakTable<Player, StrongBox<bool>>();
        

        private static void Player_SwallowObject(On.Player.orig_SwallowObject orig, Player self, int grasp)
        {
            bool isCrafting = true;

            // Are we Artificer?
            if (!ModManager.MSC || self.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                isCrafting = false;

            // Is the held object craftable?
            if (grasp >= 0 && self.grasps[grasp] != null)
                if (!IsObjectCraftable(self.grasps[grasp].grabbed.abstractPhysicalObject))
                    isCrafting = false;

            // Do we have the food necessary to craft?
            if (self.FoodInStomach <= 0)
                isCrafting = false;

            // If we can't always craft, are the inputs correct?
            if (!Options.alwaysInstantCraft.Value)
                if ((self.input[0].y != 1 && Options.holdUpToCraft.Value) || (self.input[0].y == 1 && !Options.holdUpToCraft.Value))
                    isCrafting = false;



            if (PlayersCrafting.TryGetValue(self, out var isCraftingStrongBox))
                isCraftingStrongBox.Value = isCrafting;

            else
                PlayersCrafting.Add(self, new StrongBox<bool>(isCrafting));


            orig(self, grasp);


            if (!isCrafting) return;

            self.Regurgitate();
        }

        private static bool IsObjectCraftable(AbstractPhysicalObject abstractObject)
        {
            AbstractPhysicalObject.AbstractObjectType type = abstractObject.type;

            if (type == AbstractPhysicalObject.AbstractObjectType.Rock) return true;

            if (type == AbstractPhysicalObject.AbstractObjectType.FlyLure) return true;

            if (type == AbstractPhysicalObject.AbstractObjectType.FlareBomb) return true;

            return false;
        }
    }
}
