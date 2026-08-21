using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Measures whether OTHER mods actually got the raised NPC cap, by reading the lengths of their static
    /// NPC-indexed arrays at runtime.
    /// <para/>
    /// <b>Why this exists.</b> <see cref="EarlyCapRaise"/> claims that raising <c>Main.maxNPCs</c> before any
    /// mod loads content makes every mod size its own arrays correctly. "Nothing crashed" is weak evidence for
    /// that: a mod whose bounds check still reads 200 also does not crash — it silently ignores every NPC above
    /// slot 199, which is the exact failure mode this whole mod exists to eliminate, and it looks identical in a
    /// log. This reads the array lengths directly, so the claim is measured rather than inferred.
    /// <para/>
    /// It doubles as the first thing to run on any future "mod X misbehaves with lots of enemies" report: a
    /// STALE line names the offending mod and field outright.
    /// <para/>
    /// <b>What it can and cannot see.</b> Only <c>static</c> array fields (including auto-property backing
    /// fields) whose length looks NPC-shaped. A mod that keeps its array in an instance field, or that hardcodes
    /// a literal 200 rather than reading the field, is invisible here — the second case being the one nothing on
    /// our side can fix anyway.
    /// <para/>
    /// <b>Keep this on debug-only paths.</b> Reading a static field forces its declaring type's class
    /// constructor to run if it has not already. Normally that is harmless — it would have run on first use
    /// anyway — but if one throws, .NET caches the failure and every later access to that type throws
    /// <c>TypeInitializationException</c> for the rest of the process. Triggering someone else's initializer at
    /// a moment of our choosing is therefore a real (if small) risk, and it is only worth taking when a human
    /// asked a diagnostic question. Today the only automatic caller is the world-load state dump, which is
    /// gated behind <c>DebugMode</c>; the filter that would remove the risk (skipping types that have a class
    /// constructor at all) would also blind this to <c>static bool[] x = new bool[Main.maxNPCs]</c>, the single
    /// most important pattern it exists to catch. So the constraint is the placement, not the code.
    /// </summary>
    internal static class ModArrayScanner
    {
        private const int VanillaCap = 200;

        private const BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>Builds the report. <paramref name="stale"/> = arrays still sized for the vanilla cap.</summary>
        public static string Build(out int stale) => Build(out stale, out _, out _);

        /// <summary>
        /// As <see cref="Build(out int)"/>, but also reports how many arrays were examined and a comma-separated
        /// list of the mods owning the stale ones — enough for the one-line summary in the world-load dump.
        /// </summary>
        public static string Build(out int stale, out int checkedCount, out string staleMods)
        {
            stale = 0;
            var staleOwners = new SortedSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            int cap = MaxNpcCapRaise.AppliedCap;

            sb.AppendLine($"cap={cap}  early raise={(EarlyCapRaise.Applied ? "yes" : "NO")}  " +
                          $"Main.maxNPCs(direct)={Main.maxNPCs}");
            sb.AppendLine("Static arrays in loaded mods whose length is NPC-shaped (200/201 = built for vanilla,");
            sb.AppendLine($"{cap}/{cap + 1} = built for the raised cap). Anything else is not an NPC array and is not listed.");
            sb.AppendLine();
            sb.AppendLine("state\tmod\tlength\tfield");

            var rows = new List<string>();

            foreach (Mod mod in ModLoader.Mods)
            {
                if (mod == null || mod.Name == "ModLoader")
                    continue;

                Assembly code;
                try { code = mod.Code; }
                catch { continue; }

                if (code == null)
                    continue;

                foreach (Type type in GetTypesSafely(code))
                {
                    if (type == null)
                        continue;

                    FieldInfo[] fields;
                    try { fields = type.GetFields(AllStatic); }
                    catch { continue; }

                    foreach (FieldInfo field in fields)
                    {
                        if (!field.FieldType.IsArray || field.FieldType.GetArrayRank() != 1)
                            continue;

                        Array value;
                        try { value = field.GetValue(null) as Array; }
                        catch { continue; } // a static whose class initializer throws is not our problem

                        if (value == null)
                            continue;

                        int length = value.Length;
                        bool isStale = length == VanillaCap || length == VanillaCap + 1;
                        bool isRaised = length == cap || length == cap + 1;
                        if (!isStale && !isRaised)
                            continue;

                        // Below the raise there is nothing to tell apart — 200 IS the correct answer then.
                        if (isStale && cap <= VanillaCap)
                            continue;

                        if (isStale)
                        {
                            stale++;
                            staleOwners.Add(mod.Name);
                        }

                        rows.Add($"{(isStale ? "STALE" : "ok")}\t{mod.Name}\t{length}\t{type.FullName}::{field.Name}");
                    }
                }
            }

            rows.Sort(StringComparer.Ordinal); // STALE sorts before ok, so the problems lead
            foreach (string row in rows)
                sb.AppendLine(row);

            sb.AppendLine();
            sb.AppendLine(rows.Count == 0
                ? "(no NPC-shaped static arrays found in any loaded mod)"
                : $"{rows.Count} array(s) matched, {stale} still sized for the vanilla cap.");

            checkedCount = rows.Count;
            staleMods = string.Join(", ", staleOwners);
            return sb.ToString();
        }

        private static IEnumerable<Type> GetTypesSafely(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // A mod referencing something unavailable still has usable types; take the ones that loaded.
                return e.Types ?? Array.Empty<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }
    }
}
