using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PickupBabyAnimals.src
{
    /// <summary>
    /// Compatibility hook for the MoreAnimals mod:
    /// Allow pickup (Sneak + right click with empty hand) for specific birds,
    /// including adults and chicks, without touching the main mod system.
    /// </summary>
    public class MoreAnimalsCompatSystem : ModSystem
    {
        private ICoreServerAPI sapi;

        // Match these entity code prefixes (path portion). Covers:
        // wildturkey, wildturkey-chick, wildturkey-<variants>, etc.
        // Plus birds from bird.zip (crows, ducks, swans, owl, sparrows, waxwings, robin).
        private static readonly HashSet<string> AllowedPrefixes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "wildturkey",
                "pheasant",
                "goldenpheasant",
                "capercaillie",
                "bird-crow",
                "crow",
                "call-duck",
                "mallard-duck",
                "pekin-duck",
                "mute-swan",
                "trumpeter-swan",
                "blackswan",
                "owl-brown",
                "house-sparrow",
                "waxwing",
                "robin",
                "acanthisittidae-baby",
                "aegothelidae-baby",
                "anatidae-baby",
                "apterygidae-baby",
                "aptornithidae-baby",
                "dinornithidae-baby",
                "emeidae-baby",
                "megalapterygidae-baby",
                "rallidae-baby",
                "strigopidae-baby",
                "snail",
                "isopod"
            };

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            if (api.Side == EnumAppSide.Server)
            {
                sapi = api as ICoreServerAPI;
                sapi.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
            }
        }

        private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            if (handling == EnumHandling.PreventDefault) return;
            if (entity == null || byPlayer == null) return;


            // Respect per-player toggle (default = enabled)
            if (!PickupBabyAnimalsModSystem.IsPickupEnabledFor(byPlayer)) return;

            // Don't allow picking up dead entities.
            if (!entity.Alive) return;

            // Only right-click interact
            if ((EnumInteractMode)mode != EnumInteractMode.Interact) return;

            var ePlayer = byPlayer.Entity as EntityPlayer;
            if (ePlayer?.Controls == null) return;

            // Mirror your existing "bag pickup" gesture:
            // Sneak + empty hand only.
            if (!ePlayer.Controls.Sneak) return;
            if (slot == null || !slot.Empty) return;

            if (!IsTargetMoreAnimalsBird(entity)) return;

            ItemStack stack = CreateCapturedStack(entity);
            if (stack == null) return;

            slot.Itemstack = stack;
            slot.MarkDirty();

            handling = EnumHandling.PreventDefault;

            // Despawn safely (no death drops)
            if (entity.World is IServerWorldAccessor sworld)
            {
                sworld.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.PickedUp });
            }
            else
            {
                entity.Die(EnumDespawnReason.PickedUp);
            }
        }

        private static bool IsTargetMoreAnimalsBird(Entity entity)
        {
            string path = entity.Code?.Path;
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string prefix in AllowedPrefixes)
            {
                if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the same capturedbaby stack format your existing mod uses,
        /// so ItemPickupBabyCreature can respawn with full attributes.
        /// </summary>
        private ItemStack CreateCapturedStack(Entity entity)
        {
            if (sapi == null) return null;

            Item capturedItem = sapi.World.GetItem(new AssetLocation("pickupbabyanimals", "capturedbaby"));
            if (capturedItem == null)
            {
                sapi.Logger.Error("PickupBabyAnimals (MoreAnimals compat): could not resolve item pickupbabyanimals:capturedbaby");
                return null;
            }

            var stack = new ItemStack(capturedItem);

            // Ensure root tree exists
            TreeAttribute root = stack.Attributes as TreeAttribute ?? new TreeAttribute();
            stack.Attributes = root;

            // Entity code to respawn later
            if (entity.Code != null)
            {
                root.SetString("pickupbabies.entityCode", entity.Code.ToShortString());
            }

            // Full attributes tree
            if (entity.Attributes is ITreeAttribute attrTree)
            {
                root["pickupbabies.attributes"] = attrTree.Clone();
            }

            // Full watched attributes tree
            if (entity.WatchedAttributes is ITreeAttribute watchedTree)
            {
                root["pickupbabies.watchedAttributes"] = watchedTree.Clone();
            }

            return stack;
        }
    }
}
