using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace StickRoast
{
    public class ItemRoastingStick : Item
    {
        private const string FoodAttribute = "food";
        private const string EatingSpoilageCheckAttribute = "stickroast:eatingSpoilageCheck";

        private JsonItemStack? eatenStick;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            if (api.World.GetItem(new AssetLocation("game:stick")) is not Item stickItem) return;

            eatenStick = new JsonItemStack
            {
                Type = EnumItemClass.Item,
                Code = stickItem.Code.Clone(),
                StackSize = 1,
                ResolvedItemstack = new ItemStack(stickItem)
            };
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            WorldInteraction[] interactions = base.GetHeldInteractionHelp(inSlot);

            foreach (WorldInteraction interaction in interactions)
            {
                if (interaction.ActionLangCode != "heldhelp-eat") continue;

                interaction.ActionLangCode = "stickroast:heldhelp-eat";
                break;
            }

            return interactions;
        }

        public override FoodNutritionProperties? GetNutritionProperties(IWorldAccessor? world, ItemStack itemstack, Entity? forEntity)
        {
            if (eatenStick == null
                || GetContainedFood(itemstack, world) is not ItemStack foodStack
                || foodStack.Collectible.GetNutritionProperties(world, foodStack, forEntity) is not FoodNutritionProperties foodNutrition)
            {
                return null;
            }

            FoodNutritionProperties nutrition = foodNutrition.Clone();

            nutrition.SaturationLossDelay = foodNutrition.SaturationLossDelay;
            // fun fact: "eating a meat on a stick" is actually documented in the game source as something calling this method would allow lol
            nutrition.EatenStack = eatenStick;

            return nutrition;
        }

        protected override bool tryEatStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, ItemStack? spawnParticleStack = null)
        {
            ItemStack? foodStack = slot.Itemstack is ItemStack roastingStick
                ? GetContainedFood(roastingStick, byEntity.World)
                : null;

            return base.tryEatStep(secondsUsed, slot, byEntity, foodStack ?? spawnParticleStack);
        }

        protected override void tryEatStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity)
        {
            if (slot.Itemstack is not ItemStack roastingStick
                || GetContainedFood(roastingStick, byEntity.World) is not ItemStack foodStack)
            {
                base.tryEatStop(secondsUsed, slot, byEntity);
                return;
            }

            if (IsFoodTooSpoiled(slot, foodStack, byEntity)) return;

            roastingStick.TempAttributes.SetBool(EatingSpoilageCheckAttribute, true);
            try
            {
                base.tryEatStop(secondsUsed, slot, byEntity);
            }
            finally
            {
                roastingStick.TempAttributes.RemoveAttribute(EatingSpoilageCheckAttribute);
            }
        }

        public override TransitionState? UpdateAndGetTransitionState(IWorldAccessor world, ItemSlot inSlot, EnumTransitionType type)
        {
            if (type != EnumTransitionType.Perish
                || inSlot.Itemstack is not ItemStack roastingStick
                || !roastingStick.TempAttributes.GetBool(EatingSpoilageCheckAttribute)
                || GetContainedFood(roastingStick, world) is not ItemStack foodStack)
            {
                return base.UpdateAndGetTransitionState(world, inSlot, type);
            }

            ItemStack probeStack = foodStack.Clone();
            DummySlot probeSlot = new(probeStack, inSlot.Inventory);
            return probeStack.Collectible.UpdateAndGetTransitionState(world, probeSlot, type);
        }

        public static bool IsFoodTooSpoiled(ItemSlot outerSlot, ItemStack foodStack, EntityAgent byEntity)
        {
            ItemStack probeStack = foodStack.Clone();
            DummySlot probeSlot = new(probeStack, outerSlot.Inventory);
            TransitionState? state = probeStack.Collectible.UpdateAndGetTransitionState(byEntity.World, probeSlot, EnumTransitionType.Perish);

            if (state?.TransitionLevel >= 1f) return true;

            return probeSlot.Itemstack == null
                || probeSlot.Itemstack.Collectible.GetNutritionProperties(byEntity.World, probeSlot.Itemstack, byEntity) == null;
        }

        public static ItemStack? GetContainedFood(ItemStack roastingStick, IWorldAccessor? world)
        {
            ItemStack? foodStack = roastingStick.Attributes.GetItemstack(FoodAttribute);
            if (foodStack?.Collectible != null) return foodStack;
            if (foodStack != null && world != null && foodStack.ResolveBlockOrItem(world)) return foodStack;
            return null;
        }

        public static void SetContainedFood(ItemStack roastingStick, ItemStack foodStack)
        {
            roastingStick.Attributes.SetItemstack(FoodAttribute, foodStack);
        }
    }
}
