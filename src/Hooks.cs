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

            if (!IsCrafting(self))
                return result;

            if (result != null)
                return result;


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
                x => x.MatchLdsfld<MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName>("Artificer"),
                x => x.Match(OpCodes.Call),
                x => x.MatchBrfalse(out _));

            ILLabel? afterHook = c.MarkLabel();

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Player, bool>>((self) =>
            {
                if (self.grasps[0] != null && self.grasps[0].grabbed.abstractPhysicalObject.type == AbstractPhysicalObject.AbstractObjectType.Spear
                    && !((AbstractSpear)self.grasps[0].grabbed.abstractPhysicalObject).explosive)
                    return true;

                for (int i = 0; i < self.grasps.Length; i++)
                {
                    if (self.grasps[i] == null) continue;


                    AbstractPhysicalObject heldObject = self.grasps[i].grabbed.abstractPhysicalObject;

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
            c.EmitDelegate<Func<Player, bool>>((player) => Options.disableForceCraft.Value);

            c.Emit(OpCodes.Brtrue, afterCraft);
        }

        private static void Player_GrabUpdateIL(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            // Normally cannot swallow when Y is held, allow it when:
            // We are Artificer
            // Up is held, not down
            c.GotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt<Player>("SpitUpCraftedObject"),
                x => x.MatchLdarg(0),
                x => x.MatchLdcI4(0));

            c.GotoPrev(MoveType.Before,
                x => x.MatchLdfld<Player>("craftingObject"));

            c.EmitDelegate<Action<Player>>((self) =>
            {
                if (self.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                    return;

                if (self.input[0].y != self.input[1].y)
                    self.swallowAndRegurgitateCounter = 0;
            });
            c.Emit(OpCodes.Ldarg_0);

            c.GotoNext(MoveType.After,
                x => x.Match(OpCodes.Ldelema),
                x => x.MatchLdfld<Player.InputPackage>("y"));

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Player, bool>>((self) =>
            {
                if (self.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                    return true;

                if (self.input[0].y != 1)
                    return true;

                return false;
            });
            
            c.Emit(OpCodes.And);
        }
        


        private static bool IsCrafting(Player self)
        {
            if (self.SlugCatClass != MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer)
                return false;

            if (self.CurrentFood <= 0)
                return false;


            if (Options.holdUpToStore.Value && self.input[0].y != 1)
                return true;

            if (!Options.holdUpToStore.Value && self.input[0].y == 1)
                return true;

            return false;
        }
    }
}
