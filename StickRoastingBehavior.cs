using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace StickRoast
{
    // original name i know but i needed something to attach to the normal stick
    public class StickRoastingBehavior : CollectibleBehavior
    {
        private const string SkewerAnimation = "skewerfood";
        private const string FoodTransferFrameAttribute = "foodTransferAtFrame";

        public StickRoastingBehavior(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
        {
            if (!firstEvent
                || byEntity.LeftHandItemSlot is not ItemSlot foodSlot
                || foodSlot.Itemstack is not ItemStack foodStack
                || !IsRoastable(byEntity.World, foodSlot, foodStack, byEntity))
            {
                return;
            }

            handHandling = EnumHandHandling.PreventDefault;
            handling = EnumHandling.PreventSubsequent;

            if (byEntity.AnimManager.IsAnimationActive(SkewerAnimation)) return;

            byEntity.AnimManager.StartAnimation(SkewerAnimation);

            float foodTransferFrame = GetFoodTransferFrame(byEntity);
            byEntity.AnimManager.RegisterFrameCallback(new AnimFrameCallback
            {
                Animation = SkewerAnimation,
                Frame = foodTransferFrame,
                Callback = () =>
                {
                    if (byEntity is EntityPlayer entityPlayer && entityPlayer.Player is IPlayer player)
                    {
                        byEntity.World.PlaySoundAt(new AssetLocation("stickroast", "sounds/squishy.ogg"), byEntity, player, false);
                    }

                    if (byEntity.World.Side == EnumAppSide.Server) TryCreateRoastingStick(slot, byEntity);
                }
            });
        }

        private void TryCreateRoastingStick(ItemSlot slot, EntityAgent byEntity)
        {
            if (byEntity.RightHandItemSlot != slot
                || slot.Itemstack?.Collectible != collObj
                || byEntity.LeftHandItemSlot is not ItemSlot foodSlot
                || foodSlot.Itemstack is not ItemStack foodStack
                || !IsRoastable(byEntity.World, foodSlot, foodStack, byEntity)
                || byEntity is not EntityPlayer entityPlayer
                || entityPlayer.Player is not IPlayer player)
            {
                return;
            }

            if (byEntity.World.GetItem(new AssetLocation("stickroast:roastingstick")) is not Item roastingStickItem) return;

            ItemStack containedFood = foodStack.Clone();
            containedFood.StackSize = 1;

            ItemStack roastingStick = new(roastingStickItem);
            ItemRoastingStick.SetContainedFood(roastingStick, containedFood);

            bool stickWillEmpty = slot.StackSize == 1;
            bool foodWillEmpty = foodSlot.StackSize == 1;

            if (!stickWillEmpty && !foodWillEmpty && !player.InventoryManager.TryGiveItemstack(roastingStick, true)) return;

            slot.TakeOut(1);
            foodSlot.TakeOut(1);

            if (stickWillEmpty)
            {
                slot.Itemstack = roastingStick;
            }
            else if (foodWillEmpty)
            {
                foodSlot.Itemstack = roastingStick;
            }

            slot.MarkDirty();
            foodSlot.MarkDirty();
        }

        private static float GetFoodTransferFrame(EntityAgent byEntity)
        {
            if (byEntity.Properties.Client.AnimationsByMetaCode.TryGetValue(SkewerAnimation, out AnimationMetaData animation))
            {
                return animation.Attributes?[FoodTransferFrameAttribute].AsFloat(0) ?? 0;
            }

            return 0;
        }

        private static bool IsRoastable(IWorldAccessor world, ItemSlot foodSlot, ItemStack foodStack, EntityAgent byEntity)
        {
            if (foodStack.Collectible.GetCombustibleProperties(world, foodStack, null) is not CombustibleProperties combustibleProps) return false;
            ItemStack? cookedStack = combustibleProps.SmeltedStack?.ResolvedItemstack;

            InventorySmelting inventory = new("stickroast", "cooking", byEntity.Api);
            inventory[1].Itemstack = foodStack;

            return combustibleProps.SmeltingType == EnumSmeltType.Cook
                && foodStack.Collectible.GetMeltingDuration(world, (ISlotProvider)inventory, inventory[1]) > 0
                && combustibleProps.SmeltedRatio == 1
                && !combustibleProps.RequiresContainer
                && cookedStack != null
                && cookedStack.StackSize == 1
                && cookedStack.Collectible.GetNutritionProperties(world, cookedStack, byEntity) != null;
        }
    }
}
