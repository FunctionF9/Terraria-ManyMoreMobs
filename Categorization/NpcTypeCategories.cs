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

        public override void PostSetupContent()
        {
            int count = NPCLoader.NPCCount;
            var table = new NpcCategory[count];
            var scratch = new NPC();

            for (int type = 0; type < count; type++)
            {
                try
                {
                    scratch.SetDefaults(type);
                    table[type] = NpcCategorizer.ClassifyByFlags(scratch);
                }
                catch
                {
                    // A misbehaving SetDefaults shouldn't break loading; default such types to Enemy.
                    table[type] = NpcCategory.Enemy;
                }
            }

            _byType = table;
        }

        public override void Unload() => _byType = null;

        /// <summary>Category for a type id; returns Enemy for unknown/out-of-range types.</summary>
        public static NpcCategory Get(int type)
        {
            if (_byType != null && type >= 0 && type < _byType.Length)
                return _byType[type];

            return NpcCategory.Enemy;
        }
    }
}
