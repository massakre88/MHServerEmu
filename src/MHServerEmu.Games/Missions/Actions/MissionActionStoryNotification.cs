using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Missions.Actions
{
    public class MissionActionStoryNotification : MissionAction
    {
        private MissionActionStoryNotificationPrototype _proto;
        public MissionActionStoryNotification(IMissionActionOwner owner, MissionActionPrototype prototype) : base(owner, prototype)
        {
            // KaZarController
            _proto = prototype as MissionActionStoryNotificationPrototype;
        }

        public override void Run()
        {
            List<Player> players = ListPool<Player>.Instance.Get();
            if (GetDistributors(_proto.SendTo, players))
            {
                foreach (Player player in players)
                    player.SendStoryNotification(_proto.StoryNotification, MissionRef);
            }
            ListPool<Player>.Instance.Return(players);
        }
    }
}
