using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PickupBabyAnimals.src
{
    public class PickupBabyAnimalsModSystem : ModSystem
    {
        private const string ConfigFileName = "babysnatcher.json";
        private const string NetworkChannelName = "pickupbabyanimals";
        private const string ToggleHotkeyCode = "pickupbabyanimals-togglepickup";

        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;

        private IServerNetworkChannel serverChannel;
        private IClientNetworkChannel clientChannel;

        private BabySnatcherConfig cfg;

        // Client-side only: local view of whether pickup is enabled.
        private bool pickupEnabledClient = true;

        // Server-side: per-player toggle state (default = enabled).
        private static readonly Dictionary<string, bool> PickupEnabledByPlayerUid = new Dictionary<string, bool>();
        private static readonly object ToggleLock = new object();

        public static bool IsPickupEnabledFor(IPlayer player)
        {
            if (player == null) return true;

            lock (ToggleLock)
            {
                if (PickupEnabledByPlayerUid.TryGetValue(player.PlayerUID, out bool enabled))
                {
                    return enabled;
                }
            }

            return true;
        }

        private static void SetPickupEnabledFor(string playerUid, bool enabled)
        {
            if (string.IsNullOrEmpty(playerUid)) return;

            lock (ToggleLock)
            {
                PickupEnabledByPlayerUid[playerUid] = enabled;
            }
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            api.RegisterItemClass("ItemPickupBabyCreature", typeof(ItemPickupBabyCreature));

            // Load config on both sides so the toggle chat notification option is available client-side too
            LoadOrCreateConfig(api);

            if (api.Side == EnumAppSide.Server)
            {
                sapi = (ICoreServerAPI)api;

                serverChannel = sapi.Network.RegisterChannel(NetworkChannelName)
                    .RegisterMessageType<TogglePickupPacket>()
                    .SetMessageHandler<TogglePickupPacket>(OnTogglePickupPacket);

                sapi.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
            }
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Ensure this mod's lang entries are loaded (helps when assets are sent by a server, or load order is funky)
            Lang.LoadLanguage(capi.Logger, capi.Assets, Lang.CurrentLocale);

            // Config (client copy) for chat notification toggle
            LoadOrCreateConfig(capi);

            clientChannel = capi.Network.RegisterChannel(NetworkChannelName)
                .RegisterMessageType<TogglePickupPacket>();

            RegisterToggleHotkey();
        }

        private void RegisterToggleHotkey()
        {
            string hotkeyName = Lang.Get("pickupbabyanimals-hotkey-toggle");
            if (hotkeyName == "pickupbabyanimals-hotkey-toggle") hotkeyName = "Toggle pick up animals";

            capi.Input.RegisterHotKey(ToggleHotkeyCode, hotkeyName, GlKeys.P, HotkeyType.GUIOrOtherControls);
            capi.Input.SetHotKeyHandler(ToggleHotkeyCode, OnToggleHotkeyPressed);
        }

        private bool OnToggleHotkeyPressed(KeyCombination comb)
        {
            pickupEnabledClient = !pickupEnabledClient;

            // Tell server about the new state (multiplayer-safe)
            try
            {
                clientChannel?.SendPacket(new TogglePickupPacket { Enabled = pickupEnabledClient });
            }
            catch
            {
                // Ignore: if networking isn't available for some reason, we still keep the local state.
            }

            if (cfg == null || cfg.ChatNotificationToggle)
            {
                string key = pickupEnabledClient ? "pickupbabyanimals-chat-toggle-on" : "pickupbabyanimals-chat-toggle-off";
                string msg = Lang.Get(key);
                capi?.ShowChatMessage(msg);
            }

            return true;
        }

        private void OnTogglePickupPacket(IServerPlayer fromPlayer, TogglePickupPacket packet)
        {
            if (fromPlayer == null || packet == null) return;

            SetPickupEnabledFor(fromPlayer.PlayerUID, packet.Enabled);
        }

        private void LoadOrCreateConfig(ICoreAPI api)
        {
            try
            {
                cfg = api.LoadModConfig<BabySnatcherConfig>(ConfigFileName);
                if (cfg == null)
                {
                    cfg = BabySnatcherConfig.CreateDefault();
                    api.StoreModConfig(cfg, ConfigFileName);
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning("PickupBabyAnimals: Failed to load {0}, using defaults. {1}", ConfigFileName, e.Message);
                cfg = BabySnatcherConfig.CreateDefault();
            }
        }

        private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            try
            {
                HandlePlayerInteractEntity(entity, byPlayer, slot, hitPosition, mode, ref handling);
            }
            catch (Exception e)
            {
                sapi?.Logger.Error("PickupBabyAnimals: exception in interaction handler: {0}", e);
            }
        }

        private void HandlePlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
        {
            if (handling == EnumHandling.PreventDefault) return;
            if (entity == null || byPlayer == null) return;

            // Respect per-player toggle (default = enabled)
            if (!IsPickupEnabledFor(byPlayer)) return;

            // Don't allow picking up dead entities (corpses can still be interacted with for other actions)
            if (!entity.Alive) return;

            if ((EnumInteractMode)mode != EnumInteractMode.Interact) return;

            var ePlayer = byPlayer.Entity as EntityPlayer;
            if (ePlayer == null) return;

            var controls = ePlayer.Controls;
            if (controls == null) return;

            // === Sprint + right click on wolf pups (unchanged) ===
            if (controls.Sprint)
            {
                if (TryPickupWolfPupAsItem(entity, ePlayer))
                {
                    handling = EnumHandling.PreventDefault;
                }

                return;
            }

            // === Sneak + right click => capturedbaby behavior ===
            if (!controls.Sneak) return;

            // Require empty hand 
            if (slot == null || !slot.Empty) return;

            var sPlayer = ePlayer.Player as IServerPlayer;

            // User config overrides:
            // - Blacklist always wins (even if code would allow pickup)
            // - Whitelist allows pickup even if not juvenile (still requires sneak + empty hand)
            if (cfg != null && cfg.IsBlacklisted(entity))
            {
                SendLocalizedError(sPlayer, "pickupbabyanimals-cantpickup");
                handling = EnumHandling.PreventDefault;
                return;
            }

            bool isWhitelisted = cfg != null && cfg.IsWhitelisted(entity);

            bool juvenile = BabySnatcherConfig.LooksLikeJuvenile(entity);
            if (!juvenile && !isWhitelisted && !IsAdultVanillaElk(entity)) return;

            // Build captured stack 
            ItemStack stack = CreateCapturedBabyStack(entity);
            if (stack == null) return;

            // Backpack routing
            if (cfg != null && cfg.RequiresBackpackSlot(entity))
            {
                ItemSlot bpSlot = FindFirstEmptyBackpackSlot(ePlayer);

                if (bpSlot == null)
                {
                    SendLocalizedError(sPlayer, "pickupbabyanimals-toobig");
                    handling = EnumHandling.PreventDefault;
                    return;
                }

                bpSlot.Itemstack = stack;
                bpSlot.MarkDirty();
            }
            else
            {
                // Default: active hotbar slot 
                slot.Itemstack = stack;
                slot.MarkDirty();
            }

            handling = EnumHandling.PreventDefault;

            // Despawn without killing
            var sworld = entity.World as IServerWorldAccessor;
            if (sworld != null)
            {
                var despawnData = new EntityDespawnData { Reason = EnumDespawnReason.PickedUp };
                sworld.DespawnEntity(entity, despawnData);
            }
        }

        private void SendLocalizedError(IServerPlayer player, string code, params object[] args)
        {
            if (player == null) return;

            // Server-side localisation so clients without the mod (or without its lang files) still see proper text
            string langKey = "ingameerror-" + code;

            string msg = Lang.GetL(player.LanguageCode, langKey, args);
            if (msg == null || msg == langKey)
            {
                msg = Lang.GetL("en", langKey, args);
            }

            if (msg == null || msg == langKey)
            {
                // Fallback: let the client try its own localisation (old behaviour)
                player.SendIngameError(code, null, args);
                return;
            }

            player.SendIngameError(code, msg);
        }

        /// <summary>
        /// Finds the first empty slot in the player's main backpack inventory.
        /// This is intentionally NOT the active hotbar slot.
        /// </summary>
        private ItemSlot FindFirstEmptyBackpackSlot(EntityPlayer ePlayer)
        {
            var plr = ePlayer?.Player as IServerPlayer;
            var invMan = plr?.InventoryManager;
            if (invMan == null) return null;

            // Main inventory is usually "backpack"
            var inv = invMan.GetOwnInventory("backpack");
            if (inv != null)
            {
                for (int i = 0; i < inv.Count; i++)
                {
                    var s = inv[i];
                    if (s != null && s.Empty) return s;
                }
            }

            // Conservative fallback: scan inventories whose ID contains "backpack"
            foreach (var kv in invMan.Inventories)
            {
                if (kv.Key == null) continue;

                string id = kv.Key.ToLowerInvariant();
                if (!id.Contains("backpack")) continue;
                if (id.Contains("hotbar") || id.Contains("craft") || id.Contains("character")) continue;

                var inv2 = kv.Value;
                if (inv2 == null) continue;

                for (int i = 0; i < inv2.Count; i++)
                {
                    var s = inv2[i];
                    if (s != null && s.Empty) return s;
                }
            }

            return null;
        }

        // === Everything below here is your existing logic (mostly unchanged) ===

        private bool TryPickupWolfPupAsItem(Entity entity, EntityPlayer ePlayer)
        {
            if (sapi == null) return false;
            if (entity == null || ePlayer == null) return false;
            if (entity.World.Side != EnumAppSide.Server) return false;

            // Respect per-player toggle
            var plr = ePlayer.Player;
            if (plr != null && !IsPickupEnabledFor(plr)) return false;

            // Don't allow pickup of a corpse.
            if (!entity.Alive) return false;

            if (!TryGetWolfPupItemCode(entity, out AssetLocation pupItemCode)) return false;

            Item pupItem = sapi.World.GetItem(pupItemCode);
            if (pupItem == null)
            {
                sapi.Logger.Error("PickupBabyAnimals: Could not resolve wolf pup item {0}", pupItemCode);
                return false;
            }

            ItemStack pupStack = new ItemStack(pupItem);

            var sPlayer = ePlayer.Player as IServerPlayer;
            // Respect config blacklist (override)
            if (cfg != null && cfg.IsBlacklisted(entity))
            {
                SendLocalizedError(sPlayer, "pickupbabyanimals-cantpickup");
                return false;
            }

            bool fullyGiven = ePlayer.TryGiveItemStack(pupStack);

            if (!fullyGiven)
            {
                SendLocalizedError(sPlayer, "pickupbabyanimals-puppy-invfull");
                return false;
            }

            entity.Die(EnumDespawnReason.PickedUp);
            return true;
        }

        private bool TryGetWolfPupItemCode(Entity entity, out AssetLocation itemCode)
        {
            itemCode = null;

            AssetLocation ecode = entity.Code;
            if (ecode == null) return false;

            string domain = ecode.Domain ?? "game";
            string path = ecode.Path ?? "";

            if (path.StartsWith("wolf-eurasian-baby-"))
            {
                itemCode = new AssetLocation(domain, "creature-" + path);
                return true;
            }

            if (path.StartsWith("creature-wolf-eurasian-baby-"))
            {
                itemCode = new AssetLocation(domain, path);
                return true;
            }

            return false;
        }

        private ItemStack CreateCapturedBabyStack(Entity entity)
        {
            if (sapi == null) return null;

            Item capturedItem = sapi.World.GetItem(new AssetLocation("pickupbabyanimals", "capturedbaby"));
            if (capturedItem == null)
            {
                sapi.Logger.Error("PickupBabyAnimals: could not resolve item pickupbabyanimals:capturedbaby");
                return null;
            }

            var stack = new ItemStack(capturedItem);

            TreeAttribute root = stack.Attributes as TreeAttribute ?? new TreeAttribute();
            stack.Attributes = root;

            if (entity.Code != null)
            {
                root.SetString("pickupbabies.entityCode", entity.Code.ToShortString());
            }

            if (entity.Attributes is ITreeAttribute attrTree)
            {
                root["pickupbabies.attributes"] = attrTree.Clone();
            }

            if (entity.WatchedAttributes is ITreeAttribute watchedTree)
            {
                root["pickupbabies.watchedAttributes"] = watchedTree.Clone();
            }

            try
            {
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    entity.ToBytes(bw, forClient: false);
                    root.SetBytes("pickupbabies.entityBytes", ms.ToArray());
                }
            }
            catch (Exception e)
            {
                sapi?.Logger?.Warning("PickupBabyAnimals: Failed to serialize entity '{0}': {1}", entity?.Code, e);
            }

            return stack;
        }

        private static bool IsAdultVanillaElk(Entity e)
        {
            if (e?.Code == null) return false;

            if (!string.Equals(e.Code.Domain, "game", StringComparison.OrdinalIgnoreCase)) return false;

            string path = e.Code.Path?.ToLowerInvariant() ?? "";

            if (path == "deer-elk-male-adult" || path == "deer-elk-female-adult") return true;

            if (path == "tameddeer-elk-male-adult" || path == "tameddeer-elk-female-adult") return true;
            if (path == "tameddeer-albinoelk-male-adult" || path == "tameddeer-albinoelk-female-adult") return true;

            return false;
        }

        [ProtoContract]
        public class TogglePickupPacket
        {
            [ProtoMember(1)]
            public bool Enabled;
        }
    }
}
