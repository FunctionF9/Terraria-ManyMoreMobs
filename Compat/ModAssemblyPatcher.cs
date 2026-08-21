using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Redirects every <c>Main.maxNPCs</c> read inside OTHER MODS' assemblies to
    /// <see cref="EngineState.NpcCap"/>, so a mod that asks the documented question gets the answer that is
    /// actually true while this mod is running.
    /// <para/>
    /// <b>Why this is needed at all.</b> This mod deliberately leaves <c>Main.maxNPCs</c> at 200 and rewrites
    /// the ENGINE to read <see cref="EngineState.NpcCap"/> instead. That keeps the engine coherent, but it means
    /// every other mod reads 200 while NPCs genuinely exist above slot 199. Their code is correct; our raised
    /// ceiling is what breaks the assumption. The failures that produces are not subtle — Boss Checklist sized
    /// a slot array to 200 and threw on every high-slot death, and Calamity does the same in
    /// <c>CalamityDrawParameterNPC</c>, throwing out of the NPC draw loop.
    /// <para/>
    /// <b>Why this substitution is safe in a way our engine patches are not.</b> Elsewhere we hunt literal
    /// <c>200</c>s and have to prove each one is an NPC bound. <c>Main.maxNPCs</c> needs no such proof: every
    /// read of it is asking "how big is the NPC array?", and under this mod the honest answer is the raised cap.
    /// One <c>ldsfld</c> becomes another <c>ldsfld</c> of the same type, so the stack is untouched and array
    /// allocations, loop bounds and index guards are all corrected by the same rewrite.
    /// <para/>
    /// <b>What it cannot fix.</b> A mod that hardcodes the literal <c>200</c> instead of reading the field is
    /// out of reach — we would be back to guessing which literals are NPC bounds, in someone else's code. In
    /// practice that is rare: Calamity has 240 field reads against 7 hardcoded loops.
    /// <para/>
    /// <b>Deliberately skipped:</b> tModLoader's own assembly (that is the engine, handled by
    /// <see cref="EngineILPatcher"/>, and it contains reads we pin to 200 on purpose — world save, for one) and
    /// this mod itself.
    /// <para/>
    /// <b>This is the fallback, not the fix.</b> <see cref="EarlyCapRaise"/> solves the same problem properly by
    /// raising the cap before anyone reads it, which also covers the case this class fundamentally cannot: an
    /// array a mod already ALLOCATED at 200 during its own load. Redirecting the guard that protects such an
    /// array without resizing the array is strictly worse than leaving both alone — it turns a silently skipped
    /// high slot into an <c>IndexOutOfRangeException</c>. Calamity's <c>CalamityDrawParameterNPC</c> is exactly
    /// that shape, and it is why this pass ships off by default.
    /// </summary>
    public class ModAssemblyPatcher : ModSystem
    {
        private static readonly FieldInfo MaxNPCsField =
            typeof(Main).GetField(nameof(Main.maxNPCs), BindingFlags.Public | BindingFlags.Static);

        private static readonly FieldInfo NpcCapField =
            typeof(EngineState).GetField(nameof(EngineState.NpcCap), BindingFlags.Public | BindingFlags.Static);

        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>The <c>ldsfld</c> opcode, whose operand is a four-byte metadata token.</summary>
        private const byte LdsfldOpcode = 0x7E;

        // Resolving a metadata token is a real lookup, and the same token repeats across every method in an
        // assembly that touches the field. Cache the verdict per module so each distinct token costs one resolve.
        private static readonly Dictionary<Module, Dictionary<int, bool>> TokenVerdicts = new();

        private static int _sitesRewritten;

        public override void PostSetupContent()
        {
            var config = ModContent.GetInstance<ManyMoreMobsConfig>();
            if (config == null || !config.PatchOtherModsNpcLimit)
                return;

            // Default cap mode: the array really is 200 slots, so every mod's reading of the field is already
            // correct and there is nothing to redirect.
            if (EngineState.NpcCap <= 200)
            {
                Mod.Logger.Info("[MMM] Mod-assembly patching skipped: cap is not raised.");
                return;
            }

            if (MaxNPCsField == null || NpcCapField == null)
            {
                Mod.Logger.Error("[MMM] Mod-assembly patching skipped: could not resolve the cap fields.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            int modsPatched = 0, methodsPatched = 0;

            foreach (Mod other in ModLoader.Mods)
            {
                if (other == null || other == Mod)
                    continue;

                // tModLoader's own "mod" IS the engine assembly. EngineILPatcher owns that, and some of its
                // maxNPCs reads are pinned to 200 on purpose; blanket-redirecting them would undo that.
                if (other.Name == "ModLoader")
                    continue;

                try
                {
                    int patched = PatchAssembly(other);
                    if (patched > 0)
                    {
                        modsPatched++;
                        methodsPatched += patched;
                        Mod.Logger.Info($"[MMM] Redirected Main.maxNPCs in {patched} method(s) of {other.Name}.");
                    }
                }
                catch (Exception e)
                {
                    // One awkward assembly must never stop us patching the rest.
                    MmmLog.Report(e, $"ModAssemblyPatcher ({other.Name})");
                }
            }

            stopwatch.Stop();
            TokenVerdicts.Clear(); // only useful during the pass

            Mod.Logger.Info($"[MMM] Mod-assembly patching complete: {methodsPatched} method(s) across " +
                            $"{modsPatched} mod(s), {_sitesRewritten} read(s) redirected, " +
                            $"{stopwatch.ElapsedMilliseconds} ms.");
        }

        /// <summary>Hook every method in the mod that reads <c>Main.maxNPCs</c>. Returns how many were hooked.</summary>
        private int PatchAssembly(Mod other)
        {
            Assembly code = other.Code;
            if (code == null)
                return 0;

            int patched = 0;
            foreach (Type type in GetTypesSafely(code))
            {
                if (type == null)
                    continue;

                foreach (MethodBase method in GetMembersSafely(type))
                {
                    // An open generic cannot be detoured, and a body-less method has nothing to rewrite.
                    if (method.ContainsGenericParameters || method.IsAbstract)
                        continue;

                    if (!ReferencesMaxNpcs(method))
                        continue;

                    try
                    {
                        MonoModHooks.Modify(method, Patch_RedirectMaxNpcs);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Mod.Logger.Warn($"[MMM] Could not redirect Main.maxNPCs in " +
                                        $"{type.FullName}::{method.Name} (skipped): {e.Message}");
                    }
                }
            }

            return patched;
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

        private static IEnumerable<MethodBase> GetMembersSafely(Type type)
        {
            MethodBase[] methods;
            MethodBase[] constructors;

            try { methods = type.GetMethods(AllDeclared); }
            catch { methods = Array.Empty<MethodBase>(); }

            try { constructors = type.GetConstructors(AllDeclared); }
            catch { constructors = Array.Empty<MethodBase>(); }

            foreach (MethodBase m in methods) yield return m;
            foreach (MethodBase c in constructors) yield return c;
        }

        /// <summary>
        /// Cheap pre-filter: does this method's IL contain an <c>ldsfld Main::maxNPCs</c>?
        /// <para/>
        /// Scanning every byte position means a real instruction can never be MISSED — the true offset is always
        /// among those tested. It can produce false positives by matching an operand byte, which costs nothing:
        /// the manipulator then finds no genuine match and leaves the method alone. Same trade as
        /// <c>EngineILPatcher.ContainsLiteral200</c>.
        /// </summary>
        private static bool ReferencesMaxNpcs(MethodBase method)
        {
            byte[] il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch { return false; }

            if (il == null)
                return false;

            Module module = method.Module;
            if (!TokenVerdicts.TryGetValue(module, out Dictionary<int, bool> verdicts))
                TokenVerdicts[module] = verdicts = new Dictionary<int, bool>();

            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != LdsfldOpcode)
                    continue;

                int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                if (IsMaxNpcsToken(module, token, verdicts))
                    return true;
            }

            return false;
        }

        private static bool IsMaxNpcsToken(Module module, int token, Dictionary<int, bool> verdicts)
        {
            // A token's top byte names its metadata table, and a field reference can only live in two of them:
            // Field (0x04) for this assembly's own fields, MemberRef (0x0A) for another assembly's. Checking
            // that first throws out almost every false positive from the byte scan for free — and the ones it
            // throws out are the expensive kind, because ResolveField answers a bad token by THROWING, and
            // tModLoader logs every first-chance exception with a full stack trace. Left unfiltered this pass
            // wrote about 1400 of them to client.log on an eight-mod list.
            uint table = (uint)token >> 24;
            if (table != 0x04 && table != 0x0A)
                return false;

            if (verdicts.TryGetValue(token, out bool known))
                return known;

            bool isMatch = false;
            try
            {
                isMatch = module.ResolveField(token) == MaxNPCsField;
            }
            catch
            {
                // A MemberRef naming a method, or a row index past the end of the table — we scanned an operand
                // byte, not an instruction. Cached below either way, so each bad token costs one throw at most.
            }

            verdicts[token] = isMatch;
            return isMatch;
        }

        /// <summary>
        /// Swap the operand of every <c>ldsfld Main::maxNPCs</c> for <see cref="EngineState.NpcCap"/>. The opcode
        /// is unchanged and both fields are <c>static int</c>, so the evaluation stack is identical.
        /// </summary>
        private static void Patch_RedirectMaxNpcs(ILContext il)
        {
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(MoveType.Before, i => i.MatchLdsfld(MaxNPCsField)))
            {
                cursor.Next.Operand = il.Import(NpcCapField);
                cursor.Index++;
                _sitesRewritten++;
            }
        }

        public override void Unload()
        {
            TokenVerdicts.Clear();
            _sitesRewritten = 0;
        }
    }
}
