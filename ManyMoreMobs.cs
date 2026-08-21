using Terraria.ModLoader;

namespace ManyMoreMobs
{
	public class ManyMoreMobs : Mod
	{
		public ManyMoreMobs()
		{
			// tModLoader constructs every Mod instance BEFORE it loads any mod's content, which is the only
			// moment at which we can raise Main.maxNPCs early enough for other mods to size their own
			// NPC-indexed arrays to fit it. See EarlyCapRaise for why that ordering is the whole point.
			//
			// Belt and braces around an already-defensive method: an exception escaping a Mod constructor
			// fails the entire mod load, and a cap raise is never worth that. Worst case we start at 200 and
			// MaxNpcCapRaise does what it always did.
			try { EarlyCapRaise.Apply(); }
			catch { /* no logger exists yet; MaxNpcCapRaise reports the resulting cap either way */ }
		}

		public override void Load()
		{
			// Set up our dedicated debug/state log files (separate from client.log) as early as possible.
			MmmLog.Init(this);
			// Mod.Logger did not exist yet during construction, so the early raise queued its output for here.
			EarlyCapRaise.FlushLog(this);
			// NOTE: config pages are listed in the in-game UI sorted by their DisplayName (see tML's
			// UIModConfig), NOT by load/registration order — so the page order is controlled purely by the
			// numeric "1. / 2. / ..." prefixes on each DisplayName in the en-US localization file.
		}
	}
}
