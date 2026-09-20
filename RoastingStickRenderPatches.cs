using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace StickRoast
{
    // alright let me explain this here i wanted rot layer texture only on the piece of food on the custom stick item, but item draw gives the whole model one overlay, so using it would apply rot to the stick as well
    // but I found that gui already has a custom renderer hook that lets me split the draw, this render hook does in fact already stores other render targets like handtp just never calls em
    // thats essentially what these patches are, im doing a little nudge so the food gets its second draw, as god intended amen
    // all of that absolutely not necessary for the mod to work btw
    public static class RoastingStickRenderPatches
    {
        [HarmonyPatch(typeof(EntityShapeRenderer), "RenderItem")]
        private static class HeldItemRenderPatch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return AddFoodDraw(
                    instructions,
                    AccessTools.Method(typeof(HeldItemRenderPatch), nameof(RenderFood)),
                    new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldarg_2),
                        new CodeInstruction(OpCodes.Ldarg_3)
                    }
                );
            }

            private static void RenderFood(EntityShapeRenderer renderer, float dt, bool isShadowPass, ItemStack stack)
            {
                if (stack.Collectible.GetBehavior<RoastingStickBehavior>() is not RoastingStickBehavior behavior
                    || renderer.entity is not EntityAgent agent)
                {
                    return;
                }

                if (ReferenceEquals(agent.RightHandItemSlot?.Itemstack, stack))
                {
                    behavior.RenderFoodLayer(agent.RightHandItemSlot, EnumItemRenderTarget.HandTp, dt, isShadowPass);
                }
                else if (ReferenceEquals(agent.LeftHandItemSlot?.Itemstack, stack))
                {
                    behavior.RenderFoodLayer(agent.LeftHandItemSlot, EnumItemRenderTarget.HandTpOff, dt, isShadowPass);
                }
            }
        }

        [HarmonyPatch(typeof(EntityItemRenderer), nameof(EntityItemRenderer.DoRender3DOpaque))]
        private static class GroundItemRenderPatch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return AddFoodDraw(
                    instructions,
                    AccessTools.Method(typeof(GroundItemRenderPatch), nameof(RenderFood)),
                    new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldarg_2)
                    }
                );
            }

            private static void RenderFood(EntityItemRenderer renderer, float dt, bool isShadowPass)
            {
                if (renderer.entity is not EntityItem entityItem
                    || entityItem.Itemstack?.Collectible.GetBehavior<RoastingStickBehavior>() is not RoastingStickBehavior behavior)
                {
                    return;
                }

                behavior.RenderFoodLayer(entityItem.Slot, EnumItemRenderTarget.Ground, dt, isShadowPass);
            }
        }

        private static IEnumerable<CodeInstruction> AddFoodDraw(IEnumerable<CodeInstruction> instructions, MethodInfo renderFoodMethod, CodeInstruction[] loadArguments)
        {
            List<CodeInstruction> code = new(instructions);
            MethodInfo renderMeshMethod = AccessTools.Method(
                typeof(IRenderAPI),
                nameof(IRenderAPI.RenderMultiTextureMesh),
                new[] { typeof(MultiTextureMeshRef), typeof(string), typeof(int) }
            );

            List<int> renderCalls = new();
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Calls(renderMeshMethod)) renderCalls.Add(i);
            }

            if (renderCalls.Count != 1)
            {
                throw new InvalidOperationException($"StickRoast expected one item mesh draw call, found {renderCalls.Count}.");
            }

            List<CodeInstruction> injected = new(loadArguments)
            {
                new CodeInstruction(OpCodes.Call, renderFoodMethod)
            };

            code.InsertRange(renderCalls[0] + 1, injected);
            return code;
        }
    }
}
