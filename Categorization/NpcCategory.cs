namespace ManyMoreMobs
{
    /// <summary>
    /// The four reservation buckets every active NPC is sorted into. Each bucket has its own
    /// configurable slot cap (see <see cref="ManyMoreMobsConfig"/>); together the caps partition the
    /// total NPC budget so reserved categories (Town, Boss) always have guaranteed space.
    /// </summary>
    public enum NpcCategory
    {
        Town,
        Boss,
        Critter,
        Enemy
    }
}
