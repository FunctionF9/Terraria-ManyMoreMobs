using Terraria.ModLoader;

namespace ManyMoreMobs
{
	public class ManyMoreMobs : Mod
	{
		public override void Load()
		{
			// Set up our dedicated debug/state log files (separate from client.log) as early as possible.
			MmmLog.Init(this);
			// NOTE: config pages are listed in the in-game UI sorted by their DisplayName (see tML's
			// UIModConfig), NOT by load/registration order — so the page order is controlled purely by the
			// numeric "1. / 2. / ..." prefixes on each DisplayName in the en-US localization file.
		}
	}
}
