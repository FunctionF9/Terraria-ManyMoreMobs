using Terraria;
using Terraria.ModLoader;

namespace ManyMoreMobs
{
    /// <summary>
    /// Precomputed type id → <see cref="NpcCategory"/> lookup, built once after all content is loaded.
    /// <para/>
    /// Some classification flags (<c>townNPC</c>, <c>boss</c>, life/damage for critters) only exist on a
    /// concrete instance after <c>SetDefaults</c>, so we run <c>SetDefaults</c> on a single scratch NPC for
    /// every loaded type (vanilla + modded) and record the resulting category. This keeps runtime
    /// classification at the spawn gate O(1) and uses the exact same flag logic as live categorization.
    /// </summary>
    public class NpcTypeCategories : ModSystem
    {
        private static NpcCategory[] _byType;
        private static bool[] _specialCritter;

        public override void PostSetupContent()
        {
            int count = NPCLoader.NPCCount;
            var table = new NpcCategory[count];
            var special = new bool[count];
            var scratch = new NPC();

            for (int type = 0; type < count; type++)
            {
                try
                {
                    scratch.SetDefaults(type);
                    table[type] = NpcCategorizer.ClassifyByFlags(scratch);
                    // "Rare" critters, by the engine's own definition (the value the Lifeform Analyzer keys
                    // off). Captures the Prismatic Lacewing, Truffle Worm and the gold/gem critters — and any
                    // modded critter that opts in — without us maintaining a hardcoded id list.
                    special[type] = table[type] == NpcCategory.Critter && scratch.rarity > 0;
                }
                catch
                {
                    // A misbehaving SetDefaults shouldn't break loading; default such types to Enemy.
                    table[type] = NpcCategory.Enemy;
                    special[type] = false;
                }
            }

            _byType = table;
            _specialCritter = special;
        }

        public override void Unload()
        {
            _byType = null;
            _specialCritter = null;
        }

        /// <summary>Category for a type id; returns Enemy for unknown/out-of-range types.</summary>
        public static NpcCategory Get(int type)
        {
            if (_byType != null && type >= 0 && type < _byType.Length)
                return _byType[type];

            return NpcCategory.Enemy;
        }

        /// <summary>
        /// True for a RARE critter (<c>rarity &gt; 0</c>) — the Prismatic Lacewing, Truffle Worm, gold and gem
        /// critters. These are one-off, often progression-gating spawns (the Lacewing summons the Empress; the
        /// Truffle Worm summons Duke Fishron), so they must not be squeezed out by a critter cap that's full of
        /// bunnies. See <see cref="NpcCategorizer.IsSpecialCritter"/>.
        /// </summary>
        public static bool IsSpecial(int type)
            => _specialCritter != null && type >= 0 && type < _specialCritter.Length && _specialCritter[type];
    }
}
