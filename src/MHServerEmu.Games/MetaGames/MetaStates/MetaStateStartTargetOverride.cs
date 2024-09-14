using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.MetaGames.MetaStates
{
    public class MetaStateStartTargetOverride : MetaState
    {
	    private MetaStateStartTargetOverridePrototype _proto;
		
        public MetaStateStartTargetOverride(MetaGame metaGame, MetaStatePrototype prototype) : base(metaGame, prototype)
        {
            _proto = prototype as MetaStateStartTargetOverridePrototype;
        }
    }
}
