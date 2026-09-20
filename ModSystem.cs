using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace StickRoast
{
    public class StickRoastSystem : ModSystem
    {
        private Harmony? harmony;

        public override void Start(ICoreAPI api)
        {
            api.RegisterItemClass("ItemRoastingStick", typeof(ItemRoastingStick));
            api.RegisterCollectibleBehaviorClass("RoastingStick", typeof(RoastingStickBehavior));
            api.RegisterCollectibleBehaviorClass("StickRoasting", typeof(StickRoastingBehavior));
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            harmony = new Harmony("stickroast.render");
            harmony.PatchAll();
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll();
            harmony = null;
        }
    }
}
