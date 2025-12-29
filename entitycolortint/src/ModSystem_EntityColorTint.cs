using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace EntityColorTint
{
public class ModSystem_EntityColorTint : ModSystem
    {
        private const string AttrRoot = "entityTintV2";
        private const string AttrHas = AttrRoot + ".has";
        private const string AttrStyle = AttrRoot + ".style";
        private const string AttrCol = AttrRoot + ".argb";

        private ICoreAPI api;
        private ICoreClientAPI capi;
        private Config cfg = Config.Default();

        private long clientTick;
        private long serverSweep;
        private long beaconTick;
        private bool clientLoggedOnce;
        private bool serverLoggedOnce;

        // ===== Juvenile color BEACONS (age-up handover) =====
        private struct Beacon
        {
            public string SpeciesKey;
            public double X, Y, Z;
            public double ExpiresMs;
            public int Argb;
            public int Style;
        }
        private readonly List<Beacon> beacons = new List<Beacon>();

        private const int BeaconPeriodMs = 1000;
        private const int BeaconKeepMs = 4000;
        private const double BeaconRadius2 = 2.5 * 2.5;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            base.Start(api);

            // Load config on BOTH server and client. (Previously it only loaded on the server, so client-side settings never applied.)
            try { cfg = api.LoadModConfig<Config>("entitycolortint.json") ?? Config.Default(); }
            catch (Exception ex) { api.Logger.Error("[EntityColorTint] Failed to load entitycolortint.json, using defaults. {0}", ex); cfg = Config.Default(); }
            try { api.StoreModConfig(cfg, "entitycolortint.json"); } catch { }

if (api.Side.IsServer())
            {
                var sapi = (ICoreServerAPI)api;
sapi.Event.OnEntitySpawn += OnSpawn_ServerAssign_Safe;
                sapi.Event.OnEntityLoaded += OnLoaded_ServerEnsure_Safe;

                serverSweep = sapi.Event.RegisterGameTickListener(Sweep_ServerFixMissing_Safe, 5000);
                beaconTick = sapi.Event.RegisterGameTickListener(Server_UpdateBeacons_Safe, BeaconPeriodMs);
            }

            if (api.Side.IsClient())
            {
                capi = (ICoreClientAPI)api;
                capi.Event.OnEntitySpawn += OnSpawn_ClientApply_Safe;
                capi.Event.LevelFinalize += OnLevelFinalize_Apply_Safe;
                clientTick = capi.Event.RegisterGameTickListener(Client_Reapply_Safe, 500);
            }
        }

        public override void Dispose()
        {
            if (api?.Side.IsServer() == true)
            {
                var sapi = (ICoreServerAPI)api;
                sapi.Event.OnEntitySpawn -= OnSpawn_ServerAssign_Safe;
                sapi.Event.OnEntityLoaded -= OnLoaded_ServerEnsure_Safe;
                if (serverSweep != 0) sapi.Event.UnregisterGameTickListener(serverSweep);
                if (beaconTick != 0) sapi.Event.UnregisterGameTickListener(beaconTick);
            }

            if (capi != null)
            {
                capi.Event.OnEntitySpawn -= OnSpawn_ClientApply_Safe;
                capi.Event.LevelFinalize -= OnLevelFinalize_Apply_Safe;
                if (clientTick != 0) capi.Event.UnregisterGameTickListener(clientTick);
            }

            base.Dispose();
        }

        // ---------------- SERVER (safe wrappers) ----------------
        private void OnSpawn_ServerAssign_Safe(Entity e)
        {
            if (!api.Side.IsServer()) return;
            if (IsPlayer(e)) return; // never affect player characters
            try
            {
                if (cfg.ServerDisableAll) { ClearTint(e); return; }
                OnSpawn_ServerAssign_Core(e);
            }
            catch (Exception ex)
            {
                if (!serverLoggedOnce) { ((ICoreServerAPI)api).Logger.Error("[EntityColorTint] OnSpawn server failed: {0}", ex); serverLoggedOnce = true; }
            }
        }

        private void OnLoaded_ServerEnsure_Safe(Entity e)
        {
            if (!api.Side.IsServer()) return;
            if (IsPlayer(e)) return; // never affect player characters
            try
            {
                if (cfg.ServerDisableAll) { ClearTint(e); return; }
                OnLoaded_ServerEnsure_Core(e);
            }
            catch (Exception ex)
            {
                if (!serverLoggedOnce) { ((ICoreServerAPI)api).Logger.Error("[EntityColorTint] OnLoaded server failed: {0}", ex); serverLoggedOnce = true; }
            }
        }

        private void Sweep_ServerFixMissing_Safe(float dt)
        {
            if (!api.Side.IsServer()) return;
            try
            {
                if (cfg.ServerDisableAll) return;
                Sweep_ServerFixMissing_Core(dt);
            }
            catch (Exception ex)
            {
                if (!serverLoggedOnce) { ((ICoreServerAPI)api).Logger.Error("[EntityColorTint] Server sweep failed: {0}", ex); serverLoggedOnce = true; }
            }
        }

        private void Server_UpdateBeacons_Safe(float _)
        {
            try { Server_UpdateBeacons_Core(); }
            catch (Exception ex)
            {
                if (!serverLoggedOnce) { ((ICoreServerAPI)api).Logger.Error("[EntityColorTint] Beacon update failed: {0}", ex); serverLoggedOnce = true; }
            }
        }

        // ---------------- SERVER (core) ----------------
        private void OnSpawn_ServerAssign_Core(Entity e)
        {
            // 0) If this adult just replaced a nearby juvenile, adopt its tint
            if (!HasTint(e) && TryConsumeBeaconFor(e, out int bcol, out int bstyle))
            {
                WriteTint(e, (Style)bstyle, bcol);
            }

            // 1) Try breeding inheritance (juveniles/newborns with nearby parents)
            if (!HasTint(e) && LooksLikeJuvenile(e))
            {
                if (TryApplyBreedingInheritance(e, out Style inhStyle, out int inhArgb))
                    WriteTint(e, inhStyle, inhArgb);
            }

            // 1b) Orphan-only fallback, gated + very-low chance
            if (!HasTint(e) && TryApplyJuvenileFallback(e, out var fbStyle, out var fbArgb))
            {
                WriteTint(e, fbStyle, fbArgb);
            }

            // 2) If still no tint, assign by spawn rules
            if (!HasTint(e))
            {
                var style = PickStyle(e, cfg, api.World.Rand);
                int argb = GenerateColorARGB(e, cfg, style, api.World.Rand);
                WriteTint(e, style, argb);
            }

            // 3) Arctic guardrail
            EnforceArcticRule(e, cfg);
        }

        private void OnLoaded_ServerEnsure_Core(Entity e)
        {
            if (!e.WatchedAttributes.GetBool(AttrHas, false) && e.Attributes.GetBool(AttrHas, false))
            {
                e.WatchedAttributes.SetBool(AttrHas, true);
                e.WatchedAttributes.SetInt(AttrStyle, e.Attributes.GetInt(AttrStyle, (int)Style.SoftHue));
                e.WatchedAttributes.SetInt(AttrCol, e.Attributes.GetInt(AttrCol, unchecked((int)0xFFFFFFFF)));
                e.WatchedAttributes.MarkPathDirty(AttrRoot);
            }
            else if (!HasTint(e))
            {
                OnSpawn_ServerAssign_Core(e);
                return;
            }

            EnforceArcticRule(e, cfg);
        }

        private void Sweep_ServerFixMissing_Core(float _)
        {
            var dict = ((ICoreServerAPI)api).World.LoadedEntities;
            foreach (var kvp in dict)
            {
                var e = kvp.Value;
                if (IsPlayer(e)) continue; // skip players entirely
                if (!HasTint(e))
                {
                    if (TryConsumeBeaconFor(e, out int bcol, out int bstyle))
                    {
                        WriteTint(e, (Style)bstyle, bcol);
                    }
                    else if (LooksLikeJuvenile(e) && TryApplyBreedingInheritance(e, out Style s, out int c))
                    {
                        WriteTint(e, s, c);
                    }
                    else if (TryApplyJuvenileFallback(e, out var fbStyle, out var fbArgb))
                    {
                        WriteTint(e, fbStyle, fbArgb);
                    }
                    else
                    {
                        var st = PickStyle(e, cfg, api.World.Rand);
                        int ar = GenerateColorARGB(e, cfg, st, api.World.Rand);
                        WriteTint(e, st, ar);
                    }
                }
                EnforceArcticRule(e, cfg);
            }
        }

        // Guardrail: keep natural arctic grayscale, but allow colored mutants if enabled
        private static void EnforceArcticRule(Entity e, Config cfg)
        {
            if (!IsArcticVariant(GetVariant(e, "type"))) return;

            var at = e.Attributes;
            var wat = e.WatchedAttributes;

            bool has = at.GetBool(AttrHas, false) || wat.GetBool(AttrHas, false);
            if (!has) return;

            int style = at.GetBool(AttrHas, false) ? at.GetInt(AttrStyle, (int)Style.Gray)
                                                   : wat.GetInt(AttrStyle, (int)Style.Gray);

            if (style == (int)Style.Mutant && cfg.AllowArcticMutations) return; // keep colorful mutants

            int argb = at.GetBool(AttrHas, false) ? at.GetInt(AttrCol, 0) : wat.GetInt(AttrCol, 0);
            if (IsNeutralGray(argb)) return;

            var rnd = e.Api.World.Rand;
            double r = rnd.NextDouble();
            Style s = r < cfg.ArcticWhiteChance ? Style.White
                     : r < cfg.ArcticWhiteChance + cfg.ArcticDarkChance ? Style.Dark
                     : Style.Gray;

            argb = GenerateNeutralARGB(cfg, s, rnd);

            at.SetBool(AttrHas, true); at.SetInt(AttrStyle, (int)s); at.SetInt(AttrCol, argb);
            wat.SetBool(AttrHas, true); wat.SetInt(AttrStyle, (int)s); wat.SetInt(AttrCol, argb); wat.MarkPathDirty(AttrRoot);
        }

        private static void ClearTint(Entity e)
        {
            e.Attributes.RemoveAttribute(AttrRoot);
            e.WatchedAttributes.RemoveAttribute(AttrRoot);
        }

        // ---------------- Beacons ----------------
        private void Server_UpdateBeacons_Core()
        {
            var sapi = (ICoreServerAPI)api;
            double now = sapi.World.ElapsedMilliseconds;

            for (int i = beacons.Count - 1; i >= 0; i--)
                if (beacons[i].ExpiresMs <= now) beacons.RemoveAt(i);

            foreach (var kv in sapi.World.LoadedEntities)
            {
                var e = kv.Value;
                if (IsPlayer(e)) continue; // players never produce/consume beacons
                if (!LooksLikeJuvenile(e)) continue;

                var c = GetARGB(e);
                if (!c.HasValue) continue;

                int style = e.WatchedAttributes.GetBool(AttrHas, false)
                    ? e.WatchedAttributes.GetInt(AttrStyle, (int)Style.Gray)
                    : e.Attributes.GetInt(AttrStyle, (int)Style.Gray);

                beacons.Add(new Beacon
                {
                    SpeciesKey = SpeciesKey(e),
                    X = e.ServerPos.X,
                    Y = e.ServerPos.Y,
                    Z = e.ServerPos.Z,
                    ExpiresMs = now + BeaconKeepMs,
                    Argb = c.Value,
                    Style = style
                });
            }
        }

        private bool TryConsumeBeaconFor(Entity e, out int argb, out int style)
        {
            argb = 0; style = (int)Style.Gray;

            string key = SpeciesKey(e);
            double ex = e.ServerPos.X, ey = e.ServerPos.Y, ez = e.ServerPos.Z;

            for (int i = beacons.Count - 1; i >= 0; i--)
            {
                var b = beacons[i];
                if (b.SpeciesKey != key) continue;

                double dx = b.X - ex, dy = b.Y - ey, dz = b.Z - ez;
                if (dx * dx + dy * dy + dz * dz > BeaconRadius2) continue;

                argb = b.Argb; style = b.Style;
                beacons.RemoveAt(i);
                return true;
            }
            return false;
        }

        // ---------------- CLIENT ----------------
        private void OnSpawn_ClientApply_Safe(Entity e)
        {
            try { if (!cfg.ClientDisableAll) ApplyClientRender(e); }
            catch (Exception ex) { if (!clientLoggedOnce) { capi.Logger.Error("[EntityColorTint] Client OnSpawn failed: {0}", ex); clientLoggedOnce = true; } }
        }

        private void OnLevelFinalize_Apply_Safe()
        {
            try
            {
                if (cfg.ClientDisableAll) return;
                foreach (var kvp in capi.World.LoadedEntities) ApplyClientRender(kvp.Value);
            }
            catch (Exception ex) { if (!clientLoggedOnce) { capi.Logger.Error("[EntityColorTint] Client LevelFinalize failed: {0}", ex); clientLoggedOnce = true; } }
        }

        private void Client_Reapply_Safe(float _)
        {
            try
            {
                if (cfg.ClientDisableAll) return;
                foreach (var kvp in capi.World.LoadedEntities) ApplyClientRender(kvp.Value);
            }
            catch (Exception ex) { if (!clientLoggedOnce) { capi.Logger.Error("[EntityColorTint] Client Reapply failed: {0}", ex); clientLoggedOnce = true; } }
        }

        private void ApplyClientRender(Entity e)
        {
            if (IsPlayer(e)) return; // never color the player model
            var c = GetARGB(e);
            if (c.HasValue) TrySetRenderColor(e, c.Value);
        }
        // Player filter
        private static bool IsPlayer(Entity e) => e is EntityPlayer;


        // ---------------- Spawn style/color ----------------
        private static Style PickStyle(Entity e, Config cfg, Random rnd)
        {
            if (IsArcticVariant(GetVariant(e, "type")))
            {
                double r = rnd.NextDouble();
                if (r < cfg.ArcticWhiteChance) return Style.White;
                r -= cfg.ArcticWhiteChance;
                if (r < cfg.ArcticDarkChance) return Style.Dark;
                return Style.Gray;
            }

            double t = rnd.NextDouble();
            if (t < cfg.WeightSoftHue) return Style.SoftHue;
            t -= cfg.WeightSoftHue;
            if (t < cfg.WeightGray) return Style.Gray;
            t -= cfg.WeightGray;
            if (t < cfg.WeightDark) return Style.Dark;
            return Style.White;
        }

        private static int GenerateColorARGB(Entity e, Config cfg, Style style, Random rnd)
        {
            if (style == Style.Gray || style == Style.Dark || style == Style.White || IsArcticVariant(GetVariant(e, "type")))
                return GenerateNeutralARGB(cfg, style, rnd);

            // SoftHue (subtle natural color)
            float hue = RandRange(rnd, -cfg.SoftHue.HueJitterDeg, cfg.SoftHue.HueJitterDeg);
            float sat = RandRange(rnd, cfg.SoftHue.SMin, cfg.SoftHue.SMax);
            float lig = RandRange(rnd, cfg.SoftHue.LMin, cfg.SoftHue.LMax);
            HslToRgb(hue < 0 ? hue + 360f : hue, sat, MathF.Min(lig, cfg.White.Max), out float r, out float g, out float b);

            float floor = cfg.Gray.Min * 0.9f;
            r = Clamp01(MathF.Max(r, floor));
            g = Clamp01(MathF.Max(g, floor));
            b = Clamp01(MathF.Max(b, floor));

            return Pack(r, g, b);
        }

        private static int GenerateNeutralARGB(Config cfg, Style style, Random rnd)
        {
            float f = style switch
            {
                Style.White => RandRange(rnd, cfg.White.Min, cfg.White.Max),
                Style.Dark => RandRange(rnd, cfg.Dark.Min, cfg.Dark.Max),
                _ => RandRange(rnd, cfg.Gray.Min, cfg.Gray.Max)
            };
            f = Clamp01(f);
            return Pack(f, f, f);
        }

        // ---------------- Genetics (inherit + mutation) ----------------
        private bool TryApplyBreedingInheritance(Entity child, out Style style, out int argb)
        {
            style = Style.Gray; argb = 0;

            var parents = FindNearbyAdultsSameSpecies(child, 16.0);
            if (parents.Count == 0) return false;

            var pcols = new List<(Style st, int argb)>();
            foreach (var p in parents)
            {
                var c = GetARGB(p);
                if (c.HasValue)
                {
                    var st = (Style)(p.WatchedAttributes.GetBool(AttrHas, false) ? p.WatchedAttributes.GetInt(AttrStyle, (int)Style.Gray) : p.Attributes.GetInt(AttrStyle, (int)Style.Gray));
                    pcols.Add((st, c.Value));
                }
            }
            if (pcols.Count == 0) return false;

            var rnd = api.World.Rand;

            int baseArgb = pcols.Count == 1
                ? BlendSlightlyWithNoise(pcols[0].argb, pcols[0].argb, rnd, cfg.BreedMixNoise, cfg.BreedOvershoot)
                : BlendSlightlyWithNoise(pcols[0].argb, pcols[1].argb, rnd, cfg.BreedMixNoise, cfg.BreedOvershoot);

            // per-generation drift (lets you push lighter/darker)
            baseArgb = DriftColor(baseArgb, rnd, cfg.BreedDrift, cfg);

            bool parentsContainMutant = pcols.Any(p => p.st == Style.Mutant);

            if (cfg.ServerEnableBreedingMutations && rnd.NextDouble() < cfg.BreedingMutationChance)
            {
                var mut = PickMutation(rnd, cfg);
                baseArgb = ApplyMutation(baseArgb, mut, rnd, cfg);

                if (parentsContainMutant) baseArgb = AmplifyHue(baseArgb, rnd, cfg.MutantAmplify);
                style = Style.Mutant;
            }
            else
            {
                if (parentsContainMutant) baseArgb = AmplifyHue(baseArgb, rnd, cfg.InheritMutantBias);
                style = ClassifyNeutralStyle(baseArgb, cfg);
            }

            // Arctic: allow colored mutants if configured
            if (IsArcticVariant(GetVariant(child, "type")))
            {
                if (!(style == Style.Mutant && cfg.AllowArcticMutations))
                {
                    baseArgb = GenerateNeutralARGB(cfg, ClampArcticStyle(style), rnd);
                    style = ClampArcticStyle(style);
                }
            }

            argb = baseArgb;
            return true;
        }

        // Juvenile fallback when no parents are found (truly orphan only)
        private bool TryApplyJuvenileFallback(Entity child, out Style style, out int argb)
        {
            style = Style.Gray; argb = 0;

            // 1) Only juveniles
            if (!LooksLikeJuvenile(child)) return false;

            // 2) Only when explicitly enabled
            if (!cfg.EnableJuvenileFallbackMutation) return false;

            // 3) Double-check there are NO adults of same species nearby (orphan-only)
            if (FindNearbyAdultsSameSpecies(child, 16.0).Count > 0) return false;

            var rnd = api.World.Rand;

            var baseStyle = PickStyle(child, cfg, rnd);
            int baseArgb = GenerateColorARGB(child, cfg, baseStyle, rnd);

            // 4) Use the *fallback* chance (clamped), never the breeding chance
            double ch = cfg.JuvenileFallbackMutationChance;
            if (!(ch >= 0 && ch <= 1)) ch = 0;                // NaN/invalid -> 0
            if (ch > 0.10) ch = 0.10;                         // hard cap 10% for safety

            bool mutated = false;
            if (cfg.ServerEnableBreedingMutations && rnd.NextDouble() < ch)
            {
                var mut = PickMutation(rnd, cfg);
                baseArgb = ApplyMutation(baseArgb, mut, rnd, cfg);
                mutated = true;
                style = Style.Mutant;
            }
            else
            {
                style = ClassifyNeutralStyle(baseArgb, cfg);
            }

            // Arctic: keep neutral unless explicit mutants are allowed
            if (IsArcticVariant(GetVariant(child, "type")))
            {
                if (!(mutated && cfg.AllowArcticMutations))
                {
                    baseArgb = GenerateNeutralARGB(cfg, ClampArcticStyle(style), rnd);
                    style = ClampArcticStyle(style);
                }
            }

            argb = baseArgb;
            return true;
        }

        private static Style ClassifyNeutralStyle(int argb, Config cfg)
        {
            (float r, float g, float b) = Unpack(argb);
            if (Math.Abs(r - g) < 1e-4 && Math.Abs(g - b) < 1e-4)
            {
                if (r <= cfg.Dark.Max) return Style.Dark;
                if (r >= cfg.White.Min) return Style.White;
                return Style.Gray;
            }
            return Style.SoftHue;
        }

        private static Style ClampArcticStyle(Style s) => (s == Style.Dark || s == Style.White) ? s : Style.Gray;

        // ---------------- Helpers (breeding/color math) ----------------
        private List<Entity> FindNearbyAdultsSameSpecies(Entity child, double radius)
        {
            string key = SpeciesKey(child);
            var adults = new List<(Entity e, double d)>();
            foreach (var kv in ((ICoreServerAPI)api).World.LoadedEntities)
            {
                var e = kv.Value;
                if (e == child) continue;
                if (SpeciesKey(e) != key) continue;

                var a = GetVariant(e, "age").ToLowerInvariant();
                var s = GetVariant(e, "stage").ToLowerInvariant();
                var l1 = GetVariant(e, "lifestage").ToLowerInvariant();
                var l2 = GetVariant(e, "lifeStage").ToLowerInvariant();
                bool isAdult = a == "adult" || s == "adult" || l1 == "adult" || l2 == "adult"
                               || (!LooksLikeJuvenile(e) && (a != "" || s != "" || l1 != "" || l2 != ""));
                if (!isAdult) continue;

                double dx = e.ServerPos.X - child.ServerPos.X;
                double dy = e.ServerPos.Y - child.ServerPos.Y;
                double dz = e.ServerPos.Z - child.ServerPos.Z;
                double dist2 = dx * dx + dy * dy + dz * dz;
                if (dist2 <= radius * radius) adults.Add((e, dist2));
            }
            return adults.OrderBy(t => t.d).Take(2).Select(t => t.e).ToList();
        }

        private static string SpeciesKey(Entity e)
        {
            string domain = e.Code?.Domain ?? "game";
            string path = e.Code?.Path ?? "";
            string root = path;
            int idx = path.IndexOf('-');
            if (idx > 0) root = path.Substring(0, idx);

            root = CanonicalRoot(root, path);
            return $"{domain}:{root}";
        }

        // Map life-stage/gender roots to a single family key (e.g., hen/rooster/chick -> chicken)
        private static string CanonicalRoot(string root, string fullPath)
        {
            string p = (fullPath ?? string.Empty).ToLowerInvariant();
            string r = (root ?? string.Empty).ToLowerInvariant();

            // Chickens
            if (r == "hen" || r == "rooster" || r == "chick" || p.Contains("chicken") || p.Contains("hen-") || p.Contains("rooster"))
                return "chicken";

            // Ducks
            if (r == "duckling" || p.Contains("duck")) return "duck";

            // Geese
            if (r == "gosling" || p.Contains("goose")) return "goose";

            // Turkeys
            if (r == "poult" || p.Contains("turkey")) return "turkey";

            // Goats
            if (r == "kid" || p.Contains("goat")) return "goat";

            // Cattle
            if (r == "calf" || p.Contains("cow") || p.Contains("cattle") || p.Contains("bull")) return "cow";

            // Sheep
            if (r == "lamb" || p.Contains("sheep") || p.Contains("ram") || p.Contains("ewe")) return "sheep";

            // Deer
            if (r == "fawn" || p.Contains("deer")) return "deer";

            // Pigs
            if (r == "piglet" || p.Contains("pig")) return "pig";

            // Wolves
            if (r == "pup" || p.Contains("wolf")) return "wolf";

            // Foxes
            if (r == "kit" || p.Contains("fox")) return "fox";

            // Bears
            if (r == "cub" || p.Contains("bear")) return "bear";

            return r;
        }

        private static int BlendSlightlyWithNoise(int a, int b, Random rnd, float mixNoise, float overshoot)
        {
            (float ar, float ag, float ab) = Unpack(a);
            (float br, float bg, float bb) = Unpack(b);

            float t = 0.5f + ((float)rnd.NextDouble() - 0.5f) * 2f * mixNoise;
            t = Math.Max(0f, Math.Min(1f, t));

            float rr = Lerp(ar, br, t);
            float rg = Lerp(ag, bg, t);
            float rb = Lerp(ab, bb, t);

            rr = Overshoot(rr, ar, br, overshoot, rnd);
            rg = Overshoot(rg, ag, bg, overshoot, rnd);
            rb = Overshoot(rb, ab, bb, overshoot, rnd);

            return Pack(rr, rg, rb);
        }

        private static float Overshoot(float child, float p1, float p2, float overshoot, Random rnd)
        {
            float lo = Math.Min(p1, p2);
            float hi = Math.Max(p1, p2);
            float range = Math.Max(hi - lo, 0.05f);
            float extra = ((float)rnd.NextDouble() - 0.5f) * 2f * overshoot * range;
            return child + extra; // clamped later
        }

        private static int DriftColor(int argb, Random rnd, float drift, Config cfg)
        {
            (float r, float g, float b) = Unpack(argb);
            float mr = 1f + ((float)rnd.NextDouble() * 2f - 1f) * drift;
            float mg = 1f + ((float)rnd.NextDouble() * 2f - 1f) * drift;
            float mb = 1f + ((float)rnd.NextDouble() * 2f - 1f) * drift;
            r = Clamp255(r * mr, cfg.BreedLowClamp, cfg.BreedHighClamp);
            g = Clamp255(g * mg, cfg.BreedLowClamp, cfg.BreedHighClamp);
            b = Clamp255(b * mb, cfg.BreedLowClamp, cfg.BreedHighClamp);
            return Pack(r, g, b);
        }

        private static int AmplifyHue(int argb, Random rnd, float amount)
        {
            if (amount <= 0) return argb;
            (float r, float g, float b) = Unpack(argb);
            float mean = (r + g + b) / 3f;
            r = mean + (r - mean) * (1f + amount);
            g = mean + (g - mean) * (1f + amount);
            b = mean + (b - mean) * (1f + amount);

            float jitter = amount * 0.25f;
            r *= 1f + ((float)rnd.NextDouble() * 2f - 1f) * jitter;
            g *= 1f + ((float)rnd.NextDouble() * 2f - 1f) * jitter;
            b *= 1f + ((float)rnd.NextDouble() * 2f - 1f) * jitter;
            return Pack(Clamp01(r), Clamp01(g), Clamp01(b));
        }

        // ---------------- Mutations ----------------
        public enum Mutation { Purple = 0, Blue = 1, Pink = 2, Green = 3, DeepBlack = 4, PureWhite = 5 }

        private static Mutation PickMutation(Random rnd, Config cfg)
        {
            double r = rnd.NextDouble();
            for (int i = 0; i < cfg.MutationWeights.Length; i++)
            {
                r -= cfg.MutationWeights[i];
                if (r <= 0) return (Mutation)i;
            }
            return Mutation.Purple;
        }

        private static int ApplyMutation(int baseArgb, Mutation m, Random rnd, Config cfg)
        {
            (float r, float g, float b) = Unpack(baseArgb);
            float neutral = MathF.Max(cfg.Gray.Min, (r + g + b) / 3f);
            float I = cfg.MutationIntensity;

            float Hi(float min, float max) => RandRange(rnd, min * I, max * I);
            float Lo(float min, float max) => RandRange(rnd, min / I, max / I);

            switch (m)
            {
                case Mutation.DeepBlack:
                    r = g = b = RandRange(rnd, 0.15f, 0.28f); break;

                case Mutation.PureWhite:
                    r = g = b = Clamp01(RandRange(rnd, cfg.White.Min + 0.08f, MathF.Min(cfg.White.Max + 0.12f, cfg.BreedHighClamp))); break;

                case Mutation.Purple:
                    r = Clamp01(neutral * Hi(1.15f, 1.35f));
                    g = Clamp01(neutral * Lo(0.55f, 0.80f));
                    b = Clamp01(neutral * Hi(1.20f, 1.40f));
                    break;

                case Mutation.Blue:
                    r = Clamp01(neutral * Lo(0.55f, 0.80f));
                    g = Clamp01(neutral * Lo(0.85f, 0.98f));
                    b = Clamp01(neutral * Hi(1.25f, 1.45f));
                    break;

                case Mutation.Pink:
                    r = Clamp01(neutral * Hi(1.25f, 1.45f));
                    g = Clamp01(neutral * Lo(0.85f, 0.98f));
                    b = Clamp01(neutral * Hi(1.05f, 1.20f));
                    break;

                case Mutation.Green:
                    r = Clamp01(neutral * Lo(0.55f, 0.80f));
                    g = Clamp01(neutral * Hi(1.25f, 1.45f));
                    b = Clamp01(neutral * Lo(0.85f, 0.98f));
                    break;
            }

            // post-mutation drift so lines intensify generation to generation (slowly)
            float d = cfg.BreedDrift * 1.4f;
            r = Clamp255(r * (1f + ((float)rnd.NextDouble() * 2f - 1f) * d), cfg.BreedLowClamp, cfg.BreedHighClamp);
            g = Clamp255(g * (1f + ((float)rnd.NextDouble() * 2f - 1f) * d), cfg.BreedLowClamp, cfg.BreedHighClamp);
            b = Clamp255(b * (1f + ((float)rnd.NextDouble() * 2f - 1f) * d), cfg.BreedLowClamp, cfg.BreedHighClamp);

            return Pack(r, g, b);
        }

        // ---------------- Utils ----------------
        private static bool HasTint(Entity e) =>
            e.WatchedAttributes.GetBool(AttrHas, false) || e.Attributes.GetBool(AttrHas, false);

        private static void WriteTint(Entity e, Style style, int argb)
        {
            e.Attributes.SetBool(AttrHas, true);
            e.Attributes.SetInt(AttrStyle, (int)style);
            e.Attributes.SetInt(AttrCol, argb);

            e.WatchedAttributes.SetBool(AttrHas, true);
            e.WatchedAttributes.SetInt(AttrStyle, (int)style);
            e.WatchedAttributes.SetInt(AttrCol, argb);
            e.WatchedAttributes.MarkPathDirty(AttrRoot);
        }

        private static int? GetARGB(Entity e)
        {
            if (e.WatchedAttributes.GetBool(AttrHas, false))
                return e.WatchedAttributes.GetInt(AttrCol, unchecked((int)0xFFFFFFFF));
            if (e.Attributes.GetBool(AttrHas, false))
                return e.Attributes.GetInt(AttrCol, unchecked((int)0xFFFFFFFF));
            return null;
        }

        private static bool IsArcticVariant(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            t = t.ToLowerInvariant();
            return t == "arctic" || t == "polar" || t == "panda";
        }

        private static bool IsNeutralGray(int argb)
        {
            (float r, float g, float b) = Unpack(argb);
            return Math.Abs(r - g) < 1e-4 && Math.Abs(g - b) < 1e-4;
        }

        private static float RandRange(Random rnd, float a, float b) => a + (float)rnd.NextDouble() * (b - a);
        private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float Clamp255(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));
        private static int Pack(float r, float g, float b)
        {
            int R = (int)(Clamp01(r) * 255f);
            int G = (int)(Clamp01(g) * 255f);
            int B = (int)(Clamp01(b) * 255f);
            if (R < 0) R = 0; if (R > 255) R = 255;
            if (G < 0) G = 0; if (G > 255) G = 255;
            if (B < 0) B = 0; if (B > 255) B = 255;
            return (255 << 24) | (R << 16) | (G << 8) | B;
        }
        private static (float r, float g, float b) Unpack(int argb) =>
            (((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f);

        private static void TrySetRenderColor(Entity e, int argb) { try { e.RenderColor = argb; } catch { } }

        // HSL → RGB (0..1)
        private static void HslToRgb(float hDeg, float s, float l, out float r, out float g, out float b)
        {
            float c = (1 - MathF.Abs(2 * l - 1)) * s;
            float h = (hDeg % 360f) / 60f;
            float x = c * (1 - MathF.Abs((h % 2) - 1));
            float m = l - c / 2f;

            float rr = 0, gg = 0, bb = 0;
            if (0 <= h && h < 1) { rr = c; gg = x; bb = 0; }
            else if (1 <= h && h < 2) { rr = x; gg = c; bb = 0; }
            else if (2 <= h && h < 3) { rr = 0; gg = c; bb = x; }
            else if (3 <= h && h < 4) { rr = 0; gg = x; bb = c; }
            else if (4 <= h && h < 5) { rr = x; gg = 0; bb = c; }
            else if (5 <= h && h <= 6) { rr = c; gg = 0; bb = x; }

            r = Clamp01(rr + m); g = Clamp01(gg + m); b = Clamp01(bb + m);
        }

        // ===== Juvenile detection (expanded list) =====
        private static bool LooksLikeJuvenile(Entity e)
        {
            string age = GetVariant(e, "age").ToLowerInvariant();
            string stage = GetVariant(e, "stage").ToLowerInvariant();
            string ls1 = GetVariant(e, "lifestage").ToLowerInvariant();
            string ls2 = GetVariant(e, "lifeStage").ToLowerInvariant();

            if (!string.IsNullOrEmpty(age) && age != "adult") return true;
            if (!string.IsNullOrEmpty(stage) && stage != "adult") return true;
            if (!string.IsNullOrEmpty(ls1) && ls1 != "adult") return true;
            if (!string.IsNullOrEmpty(ls2) && ls2 != "adult") return true;

            string path = e.Code?.Path?.ToLowerInvariant() ?? "";
            string[] juvenileHints = {
                "baby","juvenile","child","young","adolescent","foal","whelp","gosling","kitten","puppy",
                "calf","cub","kit","fawn","lamb","kid","pup","offspring","cygnet","joey","piglet","cria","eyas","leveret",
                "chick","puggle","puggles","squab","owlet","spiderling","hatchling","duckling","gosling","poult","pullet","cockerel"
            };
            foreach (var hint in juvenileHints) if (path.Contains(hint)) return true;
            return false;
        }

        private static string GetVariant(Entity e, string name)
        {
            try
            {
                var dict = e.Properties?.Variant;
                if (dict != null && dict.TryGetValue(name, out string val) && !string.IsNullOrEmpty(val)) return val;
            }
            catch { }
            string v = e.WatchedAttributes.GetString(name, "");
            if (!string.IsNullOrEmpty(v)) return v;
            v = e.Attributes.GetString(name, "");
            return v ?? "";
        }
    }
}
