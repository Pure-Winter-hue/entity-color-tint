using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace PickupBabyAnimals.src
{
    /// <summary>
    /// Stored in ModConfig/babysnatcher.json
    /// - Blacklist: species you can never pick up (overrides everything)
    /// - Whitelist: species you can always pick up (even if not juvenile)
    /// - BackpackList: species that must go into backpack inventory (not the held slot)
    /// - BackpackDomainsIfJuvenile: any juvenile from these domains requires backpack inventory
    ///
    /// Matching rules (simple + forgiving):
    /// - entries containing '*' are treated like globs (wildcards)
    /// - entries starting with "domain:" match entity.Code.Domain
    /// - entries starting with "code:" match the full "domain:path" (supports '*')
    /// - otherwise: substring match against domain, path, "domain:path", "domain-path", and age/stage/lifestage variants
    ///   (so "draconis" matches everything from that mod, and "draconis-icewyvern-*" works too)
    /// </summary>
    public class BabySnatcherConfig
    {
        public List<string> Blacklist { get; set; } = new();
        /// <summary>
        /// Whitelist: entities that can be picked up even if they are not juvenile.
        /// Uses the same matching rules as Blacklist (substring, glob "*" wildcards, full "domain:path", or just "domain").
        /// Examples: "icewyvern", "draconis", "draconis:icewyvern-*", "draconis-icewyvern-*"
        /// </summary>
        public List<string> Whitelist { get; set; } = new();
        public List<string> BackpackList { get; set; } = new();
        public List<string> BackpackDomainsIfJuvenile { get; set; } = new() { "draconis" };

        /// <summary>
        /// If true, pressing the toggle hotkey will print an enabled/disabled message to chat.
        /// Set to false to suppress chat notifications.
        /// </summary>
        public bool ChatNotificationToggle { get; set; } = true;


        public static BabySnatcherConfig CreateDefault()
        {
            return new BabySnatcherConfig
            {
                // Heuristic “nope” list (hostiles, venomous, drifters, etc.)
                // Tweak freely in babysnatcher.json.
                Blacklist = new List<string>
                {
                    // hostiles / world mobs
                    "drifter", "locust", "shiver", "bell", 

                    // blacklist example
                    "whale", 

                    // venom / creepy crawlies
                    "spider", "scorpion", "snake", "wasp", "hornet"
                },

                // Always pick up (even if not juvenile). Empty by default.
                Whitelist = new List<string>(),


                // Must go into backpack inventory
                // plus juvenile animals from draconis (handled via BackpackDomainsIfJuvenile).
                BackpackList = new List<string>
                {
                    "foal",
                    "elephant",
                    "cow", 
                    "calf",
                    "aurochs",
                    "bison"
                },

                BackpackDomainsIfJuvenile = new List<string> { "draconis" },

                ChatNotificationToggle = true
            };
        }

        public bool IsBlacklisted(Entity e)
        {
            return MatchesAny(e, Blacklist);
        }

        

        public bool IsWhitelisted(Entity e)
        {
            return MatchesAny(e, Whitelist);
        }
public bool RequiresBackpackSlot(Entity e)
        {
            if (e?.Code == null) return false;

            // Any juvenile from certain domains 
            if (BackpackDomainsIfJuvenile != null && BackpackDomainsIfJuvenile.Count > 0)
            {
                string domain = (e.Code.Domain ?? "").ToLowerInvariant();
                foreach (var d in BackpackDomainsIfJuvenile)
                {
                    if (!string.IsNullOrWhiteSpace(d) && domain == d.Trim().ToLowerInvariant() && LooksLikeJuvenile(e))
                    {
                        return true;
                    }
                }
            }

            // Explicit backpack list matches
            return MatchesAny(e, BackpackList);
        }

        private static bool MatchesAny(Entity e, List<string> rules)
        {
            if (e?.Code == null || rules == null || rules.Count == 0) return false;

            string domain = (e.Code.Domain ?? "").ToLowerInvariant();
            string path = (e.Code.Path ?? "").ToLowerInvariant();
            string code = $"{domain}:{path}";

            // include common “age/stage” variants for mod compatibility
            string age = GetVariant(e, "age").ToLowerInvariant();
            string stage = GetVariant(e, "stage").ToLowerInvariant();
            string ls1 = GetVariant(e, "lifestage").ToLowerInvariant();
            string ls2 = GetVariant(e, "lifeStage").ToLowerInvariant();

            // include displayed name (so "calf" matches "Moose calf (male)")
            string dispName = "";
            try { dispName = (e.GetName() ?? "").ToLowerInvariant(); } catch { /* ignore */ }

            string[] hay =
            {
        domain,
        path,
        code,

        // allow "domain-path-*" patterns (domains optional, ':' not required)
        $"{domain}-{path}",
        $"{domain}/{path}",

        age, stage, ls1, ls2,
        dispName
    };

            foreach (var raw in rules)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string rule = raw.Trim().ToLowerInvariant();

                // domain:draconis
                if (rule.StartsWith("domain:"))
                {
                    var rd = rule.Substring("domain:".Length).Trim();
                    if (!string.IsNullOrEmpty(rd) && domain == rd) return true;
                    continue;
                }

                // code:game:wolf-eurasian-baby-*
                if (rule.StartsWith("code:"))
                {
                    var rc = rule.Substring("code:".Length).Trim();
                    if (!string.IsNullOrEmpty(rc) && GlobMatch(code, rc)) return true;
                    continue;
                }

                // if rule contains ":" treat it as a full domain:path glob by default
                // so "modid:creatures-wolf-*" works without needing "code:"
                if (rule.Contains(":"))
                {
                    if (GlobMatch(code, rule)) return true;
                    continue;
                }

                bool isGlob = rule.Contains("*");

                for (int i = 0; i < hay.Length; i++)
                {
                    if (string.IsNullOrEmpty(hay[i])) continue;

                    if (isGlob)
                    {
                        if (GlobMatch(hay[i], rule)) return true;
                    }
                    else
                    {
                        if (hay[i].Contains(rule)) return true;
                    }
                }
            }

            return false;
        }


        private static bool GlobMatch(string text, string glob)
        {
            // convert glob (*) to regex (.*)
            string pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Juvenile detection (same approach) 
        public static bool LooksLikeJuvenile(Entity e)
        {
            if (e == null) return false;

            string[] juvenileWords =
            {
                "baby","juvenile","child","young","youth","adolescent",
                "calf","kid","fawn","lamb","foal","pup","puppy",
                "kitten","kit","cub","gosling","duckling","chick",
                "piglet","joey","offspring","cygnet","cria","eyas",
                "leveret","puggle","puggles","squab","owlet",
                "spiderling","hatchling","poult","pullet","whelp"
            };

            string age = GetVariant(e, "age").ToLowerInvariant();
            string stage = GetVariant(e, "stage").ToLowerInvariant();
            string ls1 = GetVariant(e, "lifestage").ToLowerInvariant();
            string ls2 = GetVariant(e, "lifeStage").ToLowerInvariant();

            if (age == "adult" || stage == "adult" || ls1 == "adult" || ls2 == "adult") return false;

            bool VariantLooksJuvenile(string v)
            {
                if (string.IsNullOrEmpty(v)) return false;
                foreach (var w in juvenileWords)
                {
                    if (v.Contains(w)) return true;
                }
                return false;
            }

            if (VariantLooksJuvenile(age) || VariantLooksJuvenile(stage) ||
                VariantLooksJuvenile(ls1) || VariantLooksJuvenile(ls2))
                return true;

            string path = e.Code?.Path?.ToLowerInvariant() ?? "";
            foreach (var w in juvenileWords)
            {
                if (path.Contains(w)) return true;
            }

            return false;
        }

        private static string GetVariant(Entity e, string name)
        {
            if (e == null) return "";

            try
            {
                var dict = e.Properties?.Variant;
                if (dict != null && dict.TryGetValue(name, out string val) && !string.IsNullOrEmpty(val))
                {
                    return val;
                }
            }
            catch { /* ignore */ }

            string v = e.WatchedAttributes?.GetString(name, "") ?? "";
            if (!string.IsNullOrEmpty(v)) return v;

            v = e.Attributes?.GetString(name, "") ?? "";
            return v ?? "";
        }
    }
}
