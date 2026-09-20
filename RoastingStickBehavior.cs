using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StickRoast
{
    public class RoastingStickBehavior : CollectibleBehavior
    {
        private const string RoastProgressAttribute = "roastProgress";
        private const string CoolingStartTemperatureAttribute = "roastCoolingTemperature";
        private const string CookDurationAttribute = "stickroast:cookDuration";
        private const string CookingTemperatureAttribute = "stickroast:cookingTemperature";
        private const string LastUseSecondsAttribute = "stickroast:lastUseSeconds";
        private const string EatingInteractionAttribute = "stickroast:eatingInteraction";
        private const string RoastingAnimation = "roastingfood";
        private const float SizzleMaxVolume = 0.7f;
        private const float SizzleRange = 10f;
        private const float SizzleReferenceDistance = 3f;

        private Dictionary<string, MultiTextureMeshRef>? foodMeshRefs;
        private readonly Dictionary<long, ILoadedSound> otherSizzles = new();
        private readonly List<long> staleSizzles = new();

        private bool clientRoastingInteraction;
        private ICoreClientAPI? clientApi;
        private ILoadedSound? sizzlingSound;
        private long otherSizzleTickId;
        private float clientRoastSecondsUsed;

        public RoastingStickBehavior(CollectibleObject collObj) : base(collObj)
        {
        }

        public override void OnLoaded(ICoreAPI api)
        {
            collObj.HeldPriorityInteract = true;

            if (api is ICoreClientAPI capi)
            {
                const int otherSizzleUpdateMs = 100;

                clientApi = capi;
                capi.Event.RegisterItemstackRenderer(collObj, RenderItemstackGui, EnumItemRenderTarget.Gui);
                otherSizzleTickId = capi.Event.RegisterGameTickListener(UpdateOtherSizzles, otherSizzleUpdateMs);
            }
        }

        public override void OnUnloaded(ICoreAPI api)
        {
            StopSizzle();
            StopOtherSizzles();

            if (clientApi != null)
            {
                clientApi.Event.UnregisterItemstackRenderer(collObj, EnumItemRenderTarget.Gui);

                if (otherSizzleTickId != 0)
                {
                    clientApi.Event.UnregisterGameTickListener(otherSizzleTickId);
                    otherSizzleTickId = 0;
                }
            }

            sizzlingSound?.Dispose();
            sizzlingSound = null;
            clientApi = null;

            if (foodMeshRefs != null)
            {
                foreach (MultiTextureMeshRef meshRef in foodMeshRefs.Values)
                {
                    meshRef.Dispose();
                }

                foodMeshRefs = null;
            }

            base.OnUnloaded(api);
        }

        private void RenderItemstackGui(ItemSlot inSlot, ItemRenderInfo renderInfo, Matrixf modelMat, double posX, double posY, double posZ, float size, int color, bool rotate = false, bool showStackSize = true)
        {
            if (clientApi == null) return;

            clientApi.Render.RenderMultiTextureMesh(renderInfo.ModelRef, "tex2d");
            RenderFoodLayer(inSlot, EnumItemRenderTarget.Gui, renderInfo.dt, false);
        }

        public void RenderFoodLayer(ItemSlot outerSlot, EnumItemRenderTarget target, float dt, bool isShadowPass)
        {
            ICoreClientAPI? capi = clientApi;
            ItemStack? roastingStick = outerSlot.Itemstack;
            if (capi == null
                || roastingStick?.Item is not Item roastingStickItem
                || ItemRoastingStick.GetContainedFood(roastingStick, capi.World) is not ItemStack foodStack
                || foodStack.Item is not Item foodItem)
            {
                return;
            }

            foodMeshRefs ??= new Dictionary<string, MultiTextureMeshRef>();

            string cacheKey = foodItem.Code.ToString();
            if (!foodMeshRefs.TryGetValue(cacheKey, out MultiTextureMeshRef? foodModelRef))
            {
                foodModelRef = capi.Render.UploadMultiTextureMesh(CreateFoodMesh(capi, roastingStickItem, foodItem, foodStack));
                foodMeshRefs[cacheKey] = foodModelRef;
            }

            if (isShadowPass)
            {
                capi.Render.RenderMultiTextureMesh(foodModelRef, "tex2d");
                return;
            }

            DummySlot foodSlot = new(foodStack, outerSlot.Inventory);
            ItemRenderInfo foodRenderInfo = capi.Render.GetItemStackRenderInfo(foodSlot, target, dt);
            IShaderProgram shader = capi.Render.CurrentActiveShader;

            shader.Uniform("overlayOpacity", foodRenderInfo.OverlayOpacity);
            if (foodRenderInfo.OverlayTexture != null && foodRenderInfo.OverlayOpacity > 0)
            {
                shader.BindTexture2D("tex2dOverlay", foodRenderInfo.OverlayTexture.TextureId, 1);
                shader.Uniform("overlayTextureSize", foodRenderInfo.OverlayTexture.Width, foodRenderInfo.OverlayTexture.Height);
                shader.Uniform("baseTextureSize", foodRenderInfo.TextureSize.Width, foodRenderInfo.TextureSize.Height);

                TextureAtlasPosition texPos = capi.Render.GetTextureAtlasPosition(foodStack);
                shader.Uniform("baseUvOrigin", texPos.x1, texPos.y1);
            }

            capi.Render.RenderMultiTextureMesh(foodModelRef, target == EnumItemRenderTarget.Gui ? "tex2d" : "tex");
            shader.Uniform("overlayOpacity", 0f);
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot, ref EnumHandling handling)
        {
            handling = EnumHandling.PassThrough;
            return new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "stickroast:heldhelp-removefood",
                    MouseButton = EnumMouseButton.Right,
                    HotKeyCode = "ctrl"
                }
            };
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
        {
            if (!firstEvent || slot.Itemstack is not ItemStack roastingStick) return;

            roastingStick.TempAttributes.RemoveAttribute(EatingInteractionAttribute);

            if (ItemRoastingStick.GetContainedFood(roastingStick, byEntity.World) is not ItemStack foodStack)
            {
                StopRoasting(byEntity);
                return;
            }

            if (byEntity.Controls.CtrlKey)
            {
                handHandling = EnumHandHandling.PreventDefault;
                handling = EnumHandling.PreventDefault;
                StopRoasting(byEntity);

                if (byEntity.LeftHandItemSlot?.Empty != true)
                {
                    if (byEntity.Api is ICoreClientAPI capi)
                    {
                        capi.TriggerIngameError(this, "offhandoccupied", Lang.Get("stickroast:offhandoccupied"));
                    }

                    return;
                }

                const string unskewerAnimation = "unskewerfood";

                if (byEntity.AnimManager.IsAnimationActive(unskewerAnimation)) return;

                byEntity.AnimManager.StartAnimation(unskewerAnimation);

                float foodTransferFrame = GetFoodTransferFrame(byEntity, unskewerAnimation);
                byEntity.AnimManager.RegisterFrameCallback(new AnimFrameCallback
                {
                    Animation = unskewerAnimation,
                    Frame = foodTransferFrame,
                    Callback = () =>
                    {
                        if (byEntity is EntityPlayer entityPlayer && entityPlayer.Player is IPlayer player)
                        {
                            byEntity.World.PlaySoundAt(new AssetLocation("stickroast", "sounds/squishy.ogg"), byEntity, player, false);
                        }

                        if (byEntity.World.Side == EnumAppSide.Server) TrySeparateFood(slot, byEntity);
                    }
                });
                return;
            }

            if (GetBurningFirepit(byEntity, blockSel) is BlockEntityFirepit firepit
                && GetRoastingProperties(byEntity.World, foodStack, byEntity) != null)
            {
                (float cookDuration, float cookingTemperature) = GetCookingSettings(byEntity, foodStack);
                if (cookDuration > 0 && cookingTemperature > 0)
                {
                    float foodTemperature = foodStack.Collectible.GetTemperature(byEntity.World, foodStack);
                    bool hadCoolingStart = roastingStick.Attributes.HasAttribute(CoolingStartTemperatureAttribute);
                    float progress = ApplyCoolingProgress(roastingStick, foodTemperature);
                    if (hadCoolingStart && byEntity.World.Side == EnumAppSide.Server) slot.MarkDirty();

                    roastingStick.TempAttributes.SetFloat(CookDurationAttribute, cookDuration);
                    roastingStick.TempAttributes.SetFloat(CookingTemperatureAttribute, cookingTemperature);
                    roastingStick.TempAttributes.SetFloat(LastUseSecondsAttribute, 0);
                    StartRoastingAnimation(byEntity);

                    if (byEntity.World.Side == EnumAppSide.Client)
                    {
                        clientRoastingInteraction = true;
                        clientRoastSecondsUsed = 0;
                        byEntity.Api.ModLoader.GetModSystem<RoastingProgressRenderer>().Begin(progress, cookDuration);
                    }

                    handHandling = EnumHandHandling.PreventDefault;
                    handling = EnumHandling.PreventDefault;
                    return;
                }
            }

            StopRoasting(byEntity);

            if (foodStack.Collectible.GetNutritionProperties(byEntity.World, foodStack, byEntity) == null)
            {
                handHandling = EnumHandHandling.PreventDefault;
                handling = EnumHandling.PreventDefault;
                return;
            }

            if (ItemRoastingStick.IsFoodTooSpoiled(slot, foodStack, byEntity))
            {
                handHandling = EnumHandHandling.PreventDefault;
                handling = EnumHandling.PreventDefault;

                if (byEntity.Api is ICoreClientAPI capi)
                {
                    capi.TriggerIngameError(this, "toospoiledtoeat", Lang.Get("stickroast:toospoiledtoeat"));
                }

                return;
            }

            roastingStick.TempAttributes.SetBool(EatingInteractionAttribute, true);
        }

        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel, ref EnumHandling handling)
        {
            if (slot.Itemstack is ItemStack eatingStick && eatingStick.TempAttributes.GetBool(EatingInteractionAttribute)) return true;

            handling = EnumHandling.PreventDefault;

            if (slot.Itemstack is not ItemStack roastingStick)
            {
                StopRoasting(byEntity);
                return false;
            }

            if (GetBurningFirepit(byEntity, blockSel) is not BlockEntityFirepit firepit
                || ItemRoastingStick.GetContainedFood(roastingStick, byEntity.World) is not ItemStack foodStack)
            {
                BeginCooling(roastingStick, byEntity.World);
                if (byEntity.World.Side == EnumAppSide.Server) slot.MarkDirty();
                StopRoasting(byEntity);
                return false;
            }

            float cookDuration = roastingStick.TempAttributes.GetFloat(CookDurationAttribute);
            float cookingTemperature = roastingStick.TempAttributes.GetFloat(CookingTemperatureAttribute);
            CombustibleProperties? roastingProperties = GetRoastingProperties(byEntity.World, foodStack, byEntity);

            // stops the annoying campfire gui from opening when roasting finishes
            if (roastingProperties == null)
            {
                StopRoastingAnimation(byEntity);

                if (byEntity.World.Side == EnumAppSide.Client)
                {
                    byEntity.Api.ModLoader.GetModSystem<RoastingProgressRenderer>().End();
                    StopSizzle();
                    return clientRoastingInteraction;
                }

                return cookDuration > 0 && !roastingStick.Attributes.HasAttribute(RoastProgressAttribute);
            }

            if (cookDuration <= 0 || cookingTemperature <= 0)
            {
                StopRoasting(byEntity);
                return false;
            }

            float progress = roastingStick.Attributes.GetFloat(RoastProgressAttribute);
            float elapsed = GetInteractionDelta(roastingStick, secondsUsed);
            float foodTemperature = HeatFood(firepit, byEntity.World, foodStack, roastingProperties, elapsed);

            if (byEntity.World.Side == EnumAppSide.Server)
            {
                if (foodTemperature >= cookingTemperature)
                {
                    progress = AddRoastProgress(roastingStick, elapsed);
                }

                if (progress >= cookDuration)
                {
                    CookContainedFood(slot, byEntity, roastingStick, foodStack);
                    StopRoastingAnimation(byEntity);
                }

                return true;
            }

            if (foodTemperature >= cookingTemperature)
            {
                clientRoastSecondsUsed += elapsed;
                UpdateSizzle(firepit.Pos, progress + clientRoastSecondsUsed, cookDuration);
            }
            else
            {
                StopSizzle();
            }

            RoastingProgressRenderer renderer = byEntity.Api.ModLoader.GetModSystem<RoastingProgressRenderer>();
            renderer.Update(clientRoastSecondsUsed);
            if (progress + clientRoastSecondsUsed >= cookDuration)
            {
                renderer.End();
                StopSizzle();
                StopRoastingAnimation(byEntity);
            }
            return true;
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel, ref EnumHandling handling)
        {
            if (slot.Itemstack is ItemStack eatingStick && eatingStick.TempAttributes.GetBool(EatingInteractionAttribute))
            {
                eatingStick.TempAttributes.RemoveAttribute(EatingInteractionAttribute);
                return;
            }

            handling = EnumHandling.PreventDefault;
            StopRoasting(byEntity);

            if (slot.Itemstack is not ItemStack roastingStick) return;

            if (byEntity.World.Side == EnumAppSide.Server)
            {
                if (GetBurningFirepit(byEntity, blockSel) is BlockEntityFirepit firepit
                    && ItemRoastingStick.GetContainedFood(roastingStick, byEntity.World) is ItemStack foodStack
                    && GetRoastingProperties(byEntity.World, foodStack, byEntity) is CombustibleProperties roastingProperties)
                {
                    float cookDuration = roastingStick.TempAttributes.GetFloat(CookDurationAttribute);
                    float cookingTemperature = roastingStick.TempAttributes.GetFloat(CookingTemperatureAttribute);
                    if (cookDuration > 0 && cookingTemperature > 0)
                    {
                        float elapsed = GetInteractionDelta(roastingStick, secondsUsed);
                        float foodTemperature = HeatFood(firepit, byEntity.World, foodStack, roastingProperties, elapsed);
                        float progress = roastingStick.Attributes.GetFloat(RoastProgressAttribute);

                        if (foodTemperature >= cookingTemperature)
                        {
                            progress = AddRoastProgress(roastingStick, elapsed);
                        }

                        if (progress >= cookDuration)
                        {
                            CookContainedFood(slot, byEntity, roastingStick, foodStack);
                        }
                    }
                }
            }

            BeginCooling(roastingStick, byEntity.World);
            if (byEntity.World.Side == EnumAppSide.Server) slot.MarkDirty();
            ClearInteractionState(roastingStick);
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel, EnumItemUseCancelReason cancelReason, ref EnumHandling handling)
        {
            if (slot.Itemstack is ItemStack eatingStick && eatingStick.TempAttributes.GetBool(EatingInteractionAttribute))
            {
                eatingStick.TempAttributes.RemoveAttribute(EatingInteractionAttribute);
                return true;
            }

            handling = EnumHandling.PreventDefault;
            StopRoasting(byEntity);

            if (slot.Itemstack is ItemStack roastingStick)
            {
                if (byEntity.World.Side == EnumAppSide.Server
                    && GetBurningFirepit(byEntity, blockSel) is BlockEntityFirepit firepit
                    && ItemRoastingStick.GetContainedFood(roastingStick, byEntity.World) is ItemStack foodStack
                    && GetRoastingProperties(byEntity.World, foodStack, byEntity) is CombustibleProperties roastingProperties)
                {
                    float cookDuration = roastingStick.TempAttributes.GetFloat(CookDurationAttribute);
                    float cookingTemperature = roastingStick.TempAttributes.GetFloat(CookingTemperatureAttribute);
                    if (cookDuration > 0 && cookingTemperature > 0)
                    {
                        float elapsed = GetInteractionDelta(roastingStick, secondsUsed);
                        float foodTemperature = HeatFood(firepit, byEntity.World, foodStack, roastingProperties, elapsed);
                        float progress = roastingStick.Attributes.GetFloat(RoastProgressAttribute);

                        if (foodTemperature >= cookingTemperature)
                        {
                            progress = AddRoastProgress(roastingStick, elapsed);
                        }

                        if (progress >= cookDuration)
                        {
                            CookContainedFood(slot, byEntity, roastingStick, foodStack);
                        }
                    }
                }

                BeginCooling(roastingStick, byEntity.World);
                if (byEntity.World.Side == EnumAppSide.Server) slot.MarkDirty();
                ClearInteractionState(roastingStick);
            }

            return true;
        }

        private static void StartRoastingAnimation(EntityAgent byEntity)
        {
            byEntity.AnimManager.StartAnimation(RoastingAnimation);
        }

        private static void StopRoastingAnimation(EntityAgent byEntity)
        {
            byEntity.AnimManager.StopAnimation(RoastingAnimation);
        }

        private void StopRoasting(EntityAgent byEntity)
        {
            StopRoastingAnimation(byEntity);

            if (byEntity.World.Side == EnumAppSide.Client)
            {
                clientRoastingInteraction = false;
                byEntity.Api.ModLoader.GetModSystem<RoastingProgressRenderer>().End();
                StopSizzle();
            }
        }

        private void UpdateSizzle(BlockPos firepitPos, float progress, float cookDuration)
        {
            if (clientApi == null) return;

            sizzlingSound ??= clientApi.World.LoadSound(new SoundParams
            {
                Location = new AssetLocation("stickroast", "sounds/sizzling.ogg"),
                ShouldLoop = true,
                DisposeOnFinish = false,
                Range = SizzleRange,
                ReferenceDistance = SizzleReferenceDistance
            });

            sizzlingSound.SetPosition(firepitPos.ToVec3f().Add(0.5f, 0.25f, 0.5f));
            sizzlingSound.SetVolume(GetSizzleVolume(progress, cookDuration));
            if (sizzlingSound.IsPlaying != true) sizzlingSound.Start();
        }

        private void StopSizzle()
        {
            if (sizzlingSound?.IsPlaying == true) sizzlingSound.Stop();
        }

        // using animations as the sound trigger because currently doesnt support stop looping sounds
        private void UpdateOtherSizzles(float _)
        {
            if (clientApi?.World.Player.Entity is not EntityPlayer localPlayer) return;

            float rangeSq = SizzleRange * SizzleRange;
            staleSizzles.Clear();

            foreach (KeyValuePair<long, ILoadedSound> entry in otherSizzles)
            {
                if (!clientApi.World.LoadedEntities.TryGetValue(entry.Key, out Entity? entity)
                    || entity is not EntityPlayer player
                    || player.Pos.Dimension != localPlayer.Pos.Dimension
                    || player.Pos.SquareDistanceTo(localPlayer.Pos) > rangeSq
                    || !player.AnimManager.IsAnimationActive(RoastingAnimation))
                {
                    entry.Value.Stop();
                    entry.Value.Dispose();
                    staleSizzles.Add(entry.Key);
                    continue;
                }

                entry.Value.SetPosition(player.Pos.XYZFloat);
            }

            foreach (long entityId in staleSizzles)
            {
                otherSizzles.Remove(entityId);
            }

            foreach (IPlayer onlinePlayer in clientApi.World.AllOnlinePlayers)
            {
                if (onlinePlayer.Entity is not EntityPlayer player
                    || player.EntityId == localPlayer.EntityId
                    || player.Pos.Dimension != localPlayer.Pos.Dimension
                    || player.Pos.SquareDistanceTo(localPlayer.Pos) > rangeSq
                    || !player.AnimManager.IsAnimationActive(RoastingAnimation)
                    || otherSizzles.ContainsKey(player.EntityId))
                {
                    continue;
                }

                ILoadedSound sound = clientApi.World.LoadSound(new SoundParams
                {
                    Location = new AssetLocation("stickroast", "sounds/sizzling.ogg"),
                    ShouldLoop = true,
                    Position = player.Pos.XYZFloat,
                    DisposeOnFinish = false,
                    Range = SizzleRange,
                    ReferenceDistance = SizzleReferenceDistance,
                    Volume = SizzleMaxVolume
                });
                sound.Start();
                otherSizzles[player.EntityId] = sound;
            }
        }

        private void StopOtherSizzles()
        {
            foreach (ILoadedSound sound in otherSizzles.Values)
            {
                sound.Stop();
                sound.Dispose();
            }

            otherSizzles.Clear();
            staleSizzles.Clear();
        }

        private static float GetSizzleVolume(float progress, float cookDuration)
        {
            const float fullVolumeProgress = 0.2f;

            return GameMath.Clamp(progress / cookDuration / fullVolumeProgress, 0f, 1f) * SizzleMaxVolume;
        }

        private static float GetFoodTransferFrame(EntityAgent byEntity, string animation)
        {
            const string foodTransferFrameAttribute = "foodTransferAtFrame";

            if (byEntity.Properties.Client.AnimationsByMetaCode.TryGetValue(animation, out AnimationMetaData animationData))
            {
                return animationData.Attributes?[foodTransferFrameAttribute].AsFloat(0) ?? 0;
            }

            return 0;
        }

        private static CombustibleProperties? GetRoastingProperties(IWorldAccessor world, ItemStack foodStack, EntityAgent byEntity)
        {
            if (foodStack.Collectible.GetCombustibleProperties(world, foodStack, null) is not CombustibleProperties combustibleProps) return null;
            ItemStack? cookedStack = combustibleProps.SmeltedStack?.ResolvedItemstack;

            return combustibleProps.SmeltingType == EnumSmeltType.Cook
                && combustibleProps.SmeltedRatio == 1
                && !combustibleProps.RequiresContainer
                && cookedStack != null
                && cookedStack.StackSize == 1
                && cookedStack.Collectible.GetNutritionProperties(world, cookedStack, byEntity) != null
                    ? combustibleProps
                    : null;
        }

        private static (float Duration, float Temperature) GetCookingSettings(EntityAgent byEntity, ItemStack foodStack)
        {
            // this will probably turn into a config
            const float durationMultiplier = 0.5f;

            InventorySmelting inventory = new("stickroast", "cooking", byEntity.Api);
            inventory[1].Itemstack = foodStack;

            return (
                foodStack.Collectible.GetMeltingDuration(byEntity.World, (ISlotProvider)inventory, inventory[1]) * durationMultiplier,
                foodStack.Collectible.GetMeltingPoint(byEntity.World, (ISlotProvider)inventory, inventory[1])
            );
        }

        private static float GetInteractionDelta(ItemStack roastingStick, float secondsUsed)
        {
            float lastSecondsUsed = roastingStick.TempAttributes.GetFloat(LastUseSecondsAttribute);
            float elapsed = Math.Max(0, secondsUsed - lastSecondsUsed);
            roastingStick.TempAttributes.SetFloat(LastUseSecondsAttribute, secondsUsed);
            return elapsed;
        }

        private static float ApplyCoolingProgress(ItemStack roastingStick, float foodTemperature)
        {
            float progress = roastingStick.Attributes.GetFloat(RoastProgressAttribute);
            if (!roastingStick.Attributes.HasAttribute(CoolingStartTemperatureAttribute)) return progress;

            float coolingStartTemperature = roastingStick.Attributes.GetFloat(CoolingStartTemperatureAttribute);
            roastingStick.Attributes.RemoveAttribute(CoolingStartTemperatureAttribute);

            float coldTemperature = GlobalConstants.CollectibleDefaultTemperature;
            float retainedHeat = coolingStartTemperature > coldTemperature
                ? GameMath.Clamp((foodTemperature - coldTemperature) / (coolingStartTemperature - coldTemperature), 0f, 1f)
                : 0f;

            progress *= retainedHeat;
            if (progress > 0) roastingStick.Attributes.SetFloat(RoastProgressAttribute, progress);
            else roastingStick.Attributes.RemoveAttribute(RoastProgressAttribute);

            return progress;
        }

        private static void BeginCooling(ItemStack roastingStick, IWorldAccessor world)
        {
            if (roastingStick.Attributes.GetFloat(RoastProgressAttribute) <= 0
                || ItemRoastingStick.GetContainedFood(roastingStick, world) is not ItemStack foodStack)
            {
                roastingStick.Attributes.RemoveAttribute(CoolingStartTemperatureAttribute);
                return;
            }

            if (!roastingStick.Attributes.HasAttribute(CoolingStartTemperatureAttribute))
            {
                roastingStick.Attributes.SetFloat(
                    CoolingStartTemperatureAttribute,
                    foodStack.Collectible.GetTemperature(world, foodStack)
                );
            }
        }

        private static float HeatFood(BlockEntityFirepit firepit, IWorldAccessor world, ItemStack foodStack, CombustibleProperties roastingProperties, float elapsed)
        {
            float oldTemperature = foodStack.Collectible.GetTemperature(world, foodStack);
            if (elapsed <= 0 || oldTemperature >= firepit.furnaceTemperature) return oldTemperature;

            float newTemperature = firepit.changeTemperature(oldTemperature, firepit.furnaceTemperature, elapsed);
            int maxTemperature = Math.Max(roastingProperties.MaxTemperature, foodStack.ItemAttributes?["maxTemperature"].AsInt(0) ?? 0);
            if (maxTemperature > 0)
            {
                newTemperature = Math.Min(maxTemperature, newTemperature);
            }

            if (newTemperature != oldTemperature)
            {
                foodStack.Collectible.SetTemperature(world, foodStack, newTemperature, false);
            }

            return newTemperature;
        }

        private static float AddRoastProgress(ItemStack roastingStick, float elapsed)
        {
            float progress = roastingStick.Attributes.GetFloat(RoastProgressAttribute) + elapsed;
            roastingStick.Attributes.SetFloat(RoastProgressAttribute, progress);
            return progress;
        }

        private static void CookContainedFood(ItemSlot slot, EntityAgent byEntity, ItemStack roastingStick, ItemStack foodStack)
        {
            float foodTemperature = foodStack.Collectible.GetTemperature(byEntity.World, foodStack);
            InventorySmelting inventory = new("stickroast", "cooking", byEntity.Api);
            inventory[1].Itemstack = foodStack.Clone();

            foodStack.Collectible.DoSmelt(byEntity.World, (ISlotProvider)inventory, inventory[1], inventory[2]);
            if (inventory[2].Itemstack is not ItemStack cookedStack) return;

            cookedStack.Collectible.SetTemperature(byEntity.World, cookedStack, foodTemperature, false);
            ItemRoastingStick.SetContainedFood(roastingStick, cookedStack);
            roastingStick.Attributes.RemoveAttribute(RoastProgressAttribute);
            roastingStick.Attributes.RemoveAttribute(CoolingStartTemperatureAttribute);
            slot.MarkDirty();
        }

        private void TrySeparateFood(ItemSlot slot, EntityAgent byEntity)
        {
            if (byEntity.RightHandItemSlot != slot
                || slot.Itemstack is not ItemStack roastingStick
                || roastingStick.Collectible != collObj
                || ItemRoastingStick.GetContainedFood(roastingStick, byEntity.World) is not ItemStack foodStack)
            {
                return;
            }

            if (byEntity.World.GetItem(new AssetLocation("game:stick")) is not Item stickItem) return;

            ItemStack removedFood = foodStack.Clone();
            ItemSlot? offhandSlot = byEntity.LeftHandItemSlot;
            if (offhandSlot?.Empty != true) return;

            // alright this here is a bit of extra friction to the player but plocked food goes into the off hand first because thats what makes the most sense visually with the animation
            offhandSlot.Itemstack = removedFood;
            offhandSlot.MarkDirty();

            slot.Itemstack = new ItemStack(stickItem);
            slot.MarkDirty();
        }

        private static void ClearInteractionState(ItemStack roastingStick)
        {
            roastingStick.TempAttributes.RemoveAttribute(CookDurationAttribute);
            roastingStick.TempAttributes.RemoveAttribute(CookingTemperatureAttribute);
            roastingStick.TempAttributes.RemoveAttribute(LastUseSecondsAttribute);
        }

        private MeshData CreateFoodMesh(ICoreClientAPI capi, Item roastingStickItem, Item foodItem, ItemStack foodStack)
        {
            const string defaultFoodScaleAttribute = "defaultFoodScale";
            const string foodScaleAttribute = "foodScale";
            const string foodPositionAttribute = "foodPosition";

            capi.Tesselator.TesselateItem(roastingStickItem, out MeshData stickMesh);
            capi.Tesselator.TesselateItem(foodItem, out MeshData foodMesh);

            float vanillaScale = BlockEntityFirepit.GetRenderProps(foodStack)?.Transform?.ScaleXYZ.X
                ?? collObj.Attributes[defaultFoodScaleAttribute].AsFloat(0.25f);
            float foodScale = vanillaScale * collObj.Attributes[foodScaleAttribute].AsFloat(1f);
            float foodPosition = collObj.Attributes[foodPositionAttribute].AsFloat(0.72f);

            AlignFoodMesh(foodMesh, stickMesh, foodScale, foodPosition);

            return foodMesh;
        }

        // Food engineering (got it? hehe sorry)
        // ok so i wasnt satisfied with a shared transform, some pieces of food had a bad translation thats why all this mesh math
        private static void AlignFoodMesh(MeshData foodMesh, MeshData stickMesh, float scale, float position)
        {
            if (foodMesh.VerticesCount == 0 || stickMesh.VerticesCount == 0) return;

            
            MeshAutopsy foodAutopsy = AnalyzeMesh(foodMesh);
            MeshAutopsy stickAutopsy = AnalyzeMesh(stickMesh);

            Vec3f stickUp = GetStableUp(stickAutopsy.LongAxis);
            Vec3f stickSide = Normalize(stickUp.Cross(stickAutopsy.LongAxis));
            stickUp = Normalize(stickAutopsy.LongAxis.Cross(stickSide));

            float distanceAlongStick = (GameMath.Clamp(position, 0f, 1f) - 0.5f) * stickAutopsy.LongExtent;
            Vec3f target = stickAutopsy.Center + stickAutopsy.LongAxis * distanceAlongStick;

            foodMesh.MatrixTransform(BuildImpalingMatrix(foodAutopsy, stickSide, stickUp, stickAutopsy.LongAxis, target, scale));
        }

        private static MeshAutopsy AnalyzeMesh(MeshData mesh)
        {
            Vec3f mean = GetMeanVertex(mesh);
            float[,] covariance = GetCovariance(mesh, mean);

            Vec3f longAxis = FindPrincipalAxis(covariance);
            MakeAxisFacePositive(longAxis);

            Vec3f wideAxis = FindSecondaryAxis(covariance, longAxis);
            Vec3f thinAxis = Normalize(longAxis.Cross(wideAxis));
            MakeAxisFacePositive(thinAxis);
            wideAxis = Normalize(thinAxis.Cross(longAxis));

            GetBounds(mesh, mean, longAxis, wideAxis, thinAxis,
                out float minLong, out float maxLong,
                out float minWide, out float maxWide,
                out float minThin, out float maxThin);

            float wideExtent = maxWide - minWide;
            float thinExtent = maxThin - minThin;
            if (thinExtent > wideExtent)
            {
                Vec3f oldWideAxis = wideAxis;
                float oldMinWide = minWide;
                float oldMaxWide = maxWide;

                wideAxis = thinAxis;
                thinAxis = oldWideAxis * -1f;
                minWide = minThin;
                maxWide = maxThin;
                minThin = -oldMaxWide;
                maxThin = -oldMinWide;
            }

            Vec3f center = mean
                + longAxis * ((minLong + maxLong) * 0.5f)
                + wideAxis * ((minWide + maxWide) * 0.5f)
                + thinAxis * ((minThin + maxThin) * 0.5f);

            return new MeshAutopsy(center, longAxis, wideAxis, thinAxis, maxLong - minLong);
        }

        private static Vec3f GetMeanVertex(MeshData mesh)
        {
            float x = 0;
            float y = 0;
            float z = 0;

            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                int offset = i * 3;
                x += mesh.xyz[offset];
                y += mesh.xyz[offset + 1];
                z += mesh.xyz[offset + 2];
            }

            float inverseCount = 1f / mesh.VerticesCount;
            return new Vec3f(x * inverseCount, y * inverseCount, z * inverseCount);
        }

        private static float[,] GetCovariance(MeshData mesh, Vec3f mean)
        {
            float xx = 0;
            float xy = 0;
            float xz = 0;
            float yy = 0;
            float yz = 0;
            float zz = 0;

            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                int offset = i * 3;
                float x = mesh.xyz[offset] - mean.X;
                float y = mesh.xyz[offset + 1] - mean.Y;
                float z = mesh.xyz[offset + 2] - mean.Z;

                xx += x * x;
                xy += x * y;
                xz += x * z;
                yy += y * y;
                yz += y * z;
                zz += z * z;
            }

            return new[,]
            {
                { xx, xy, xz },
                { xy, yy, yz },
                { xz, yz, zz }
            };
        }

        private static Vec3f FindPrincipalAxis(float[,] covariance)
        {
            int strongestAxis = covariance[1, 1] > covariance[0, 0] ? 1 : 0;
            if (covariance[2, 2] > covariance[strongestAxis, strongestAxis]) strongestAxis = 2;

            Vec3f axis = strongestAxis switch
            {
                0 => new Vec3f(1, 0, 0),
                1 => new Vec3f(0, 1, 0),
                _ => new Vec3f(0, 0, 1)
            };

            for (int i = 0; i < 12; i++)
            {
                Vec3f next = Multiply(covariance, axis);
                if (next.Length() < 0.000001f) break;
                axis = Normalize(next);
            }

            return axis;
        }

        private static Vec3f FindSecondaryAxis(float[,] covariance, Vec3f longAxis)
        {
            Vec3f axis = GetLeastParallelWorldAxis(longAxis);
            axis = Normalize(axis - longAxis * axis.Dot(longAxis));

            for (int i = 0; i < 12; i++)
            {
                Vec3f next = Multiply(covariance, axis);
                next -= longAxis * next.Dot(longAxis);
                if (next.Length() < 0.000001f) break;
                axis = Normalize(next);
            }

            return axis;
        }

        private static void GetBounds(
            MeshData mesh,
            Vec3f mean,
            Vec3f longAxis,
            Vec3f wideAxis,
            Vec3f thinAxis,
            out float minLong,
            out float maxLong,
            out float minWide,
            out float maxWide,
            out float minThin,
            out float maxThin)
        {
            minLong = minWide = minThin = float.MaxValue;
            maxLong = maxWide = maxThin = float.MinValue;

            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                int offset = i * 3;
                Vec3f relative = new Vec3f(
                    mesh.xyz[offset] - mean.X,
                    mesh.xyz[offset + 1] - mean.Y,
                    mesh.xyz[offset + 2] - mean.Z
                );

                float alongLong = relative.Dot(longAxis);
                float alongWide = relative.Dot(wideAxis);
                float alongThin = relative.Dot(thinAxis);

                minLong = Math.Min(minLong, alongLong);
                maxLong = Math.Max(maxLong, alongLong);
                minWide = Math.Min(minWide, alongWide);
                maxWide = Math.Max(maxWide, alongWide);
                minThin = Math.Min(minThin, alongThin);
                maxThin = Math.Max(maxThin, alongThin);
            }
        }

        private static float[] BuildImpalingMatrix(
            MeshAutopsy food,
            Vec3f targetLong,
            Vec3f targetWide,
            Vec3f targetThin,
            Vec3f targetCenter,
            float scale)
        {
            float m00 = scale * (targetLong.X * food.LongAxis.X + targetWide.X * food.WideAxis.X + targetThin.X * food.ThinAxis.X);
            float m01 = scale * (targetLong.X * food.LongAxis.Y + targetWide.X * food.WideAxis.Y + targetThin.X * food.ThinAxis.Y);
            float m02 = scale * (targetLong.X * food.LongAxis.Z + targetWide.X * food.WideAxis.Z + targetThin.X * food.ThinAxis.Z);
            float m10 = scale * (targetLong.Y * food.LongAxis.X + targetWide.Y * food.WideAxis.X + targetThin.Y * food.ThinAxis.X);
            float m11 = scale * (targetLong.Y * food.LongAxis.Y + targetWide.Y * food.WideAxis.Y + targetThin.Y * food.ThinAxis.Y);
            float m12 = scale * (targetLong.Y * food.LongAxis.Z + targetWide.Y * food.WideAxis.Z + targetThin.Y * food.ThinAxis.Z);
            float m20 = scale * (targetLong.Z * food.LongAxis.X + targetWide.Z * food.WideAxis.X + targetThin.Z * food.ThinAxis.X);
            float m21 = scale * (targetLong.Z * food.LongAxis.Y + targetWide.Z * food.WideAxis.Y + targetThin.Z * food.ThinAxis.Y);
            float m22 = scale * (targetLong.Z * food.LongAxis.Z + targetWide.Z * food.WideAxis.Z + targetThin.Z * food.ThinAxis.Z);

            float tx = targetCenter.X - (m00 * food.Center.X + m01 * food.Center.Y + m02 * food.Center.Z);
            float ty = targetCenter.Y - (m10 * food.Center.X + m11 * food.Center.Y + m12 * food.Center.Z);
            float tz = targetCenter.Z - (m20 * food.Center.X + m21 * food.Center.Y + m22 * food.Center.Z);

            return new[]
            {
                m00, m10, m20, 0,
                m01, m11, m21, 0,
                m02, m12, m22, 0,
                tx, ty, tz, 1
            };
        }

        private static Vec3f GetStableUp(Vec3f longAxis)
        {
            Vec3f up = Math.Abs(longAxis.Y) < 0.9f ? new Vec3f(0, 1, 0) : new Vec3f(0, 0, 1);
            return Normalize(up - longAxis * up.Dot(longAxis));
        }

        private static Vec3f GetLeastParallelWorldAxis(Vec3f axis)
        {
            float x = Math.Abs(axis.X);
            float y = Math.Abs(axis.Y);
            float z = Math.Abs(axis.Z);

            if (x <= y && x <= z) return new Vec3f(1, 0, 0);
            if (y <= z) return new Vec3f(0, 1, 0);
            return new Vec3f(0, 0, 1);
        }

        private static void MakeAxisFacePositive(Vec3f axis)
        {
            float x = Math.Abs(axis.X);
            float y = Math.Abs(axis.Y);
            float z = Math.Abs(axis.Z);

            float strongest = x >= y && x >= z ? axis.X : y >= z ? axis.Y : axis.Z;
            if (strongest < 0) axis.Negate();
        }

        private static Vec3f Multiply(float[,] matrix, Vec3f vector)
        {
            return new Vec3f(
                matrix[0, 0] * vector.X + matrix[0, 1] * vector.Y + matrix[0, 2] * vector.Z,
                matrix[1, 0] * vector.X + matrix[1, 1] * vector.Y + matrix[1, 2] * vector.Z,
                matrix[2, 0] * vector.X + matrix[2, 1] * vector.Y + matrix[2, 2] * vector.Z
            );
        }

        private static Vec3f Normalize(Vec3f vector)
        {
            float length = vector.Length();
            return length > 0.000001f
                ? new Vec3f(vector.X / length, vector.Y / length, vector.Z / length)
                : new Vec3f(1, 0, 0);
        }

        private static BlockEntityFirepit? GetBurningFirepit(EntityAgent byEntity, BlockSelection? blockSel)
        {
            if (blockSel == null) return null;

            return byEntity.World.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityFirepit { IsBurning: true } firepit
                ? firepit
                : null;
        }

        private struct MeshAutopsy
        {
            public Vec3f Center;
            public Vec3f LongAxis;
            public Vec3f WideAxis;
            public Vec3f ThinAxis;
            public float LongExtent;

            public MeshAutopsy(Vec3f center, Vec3f longAxis, Vec3f wideAxis, Vec3f thinAxis, float longExtent)
            {
                Center = center;
                LongAxis = longAxis;
                WideAxis = wideAxis;
                ThinAxis = thinAxis;
                LongExtent = longExtent;
            }
        }
    }
}
