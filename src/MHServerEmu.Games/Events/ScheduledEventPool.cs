using System.Text;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;

namespace MHServerEmu.Games.Events
{
    /// <summary>
    /// Specialized pool for managing reusable object instances for <see cref="EventScheduler"/>.
    /// </summary>
    public class ScheduledEventPool
    {
        private readonly Dictionary<Type, IObjectPool> _eventPools = new();
        private readonly EventListPool _eventListPool = new();

        /// <summary>
        /// Constructs a new <see cref="ScheduledEventPool"/> instance.
        /// </summary>
        public ScheduledEventPool() { }

        /// <summary>
        /// Retrieves or creates a new <see cref="ScheduledEvent"/> instance of subtype <typeparamref name="T"/>.
        /// </summary>
        public T Get<T>() where T: ScheduledEvent, new()
        {
            Type type = typeof(T);
            if (_eventPools.TryGetValue(type, out IObjectPool eventPool) == false)
            {
                eventPool = new EventPool<T>();
                _eventPools.Add(type, eventPool);
            }

            return ((EventPool<T>)eventPool).Get();
        }

        /// <summary>
        /// Returns a <see cref="ScheduledEvent"/> instance to the pool.
        /// </summary>
        public void Return(ScheduledEvent @event)
        {
            // All events returned to the pool need to be created by the pool. If we don't have a node for this type, this event must have been created somewhere else.
            Type type = @event.GetType();
            if (!Verify.IsTrue(_eventPools.TryGetValue(type, out IObjectPool eventPool))) return;

            eventPool.ReturnUnsafe(@event);
        }

        /// <summary>
        /// Retrieves or creates a new <see cref="LinkedList{T}"/> instance.
        /// </summary>
        public LinkedList<ScheduledEvent> GetList()
        {
            return _eventListPool.Get();
        }

        /// <summary>
        /// Returns a <see cref="LinkedList{T}"/> instance to the pool.
        /// </summary>
        public void ReturnList(LinkedList<ScheduledEvent> eventList)
        {
            _eventListPool.Return(eventList);
        }

        /// <summary>
        /// Returns a <see cref="string"/> representing the current state of this <see cref="ScheduledEventPool"/> instance.
        /// </summary>
        public string GetReportString()
        {
            StringBuilder sb = new();

            sb.AppendLine("Name\tActive\tInactive\tTotal");

            // Accuracy > efficiency here, so recalculate all counts using the data from actual subpools
            int activeSum = 0;
            int inactiveSum = 0;
            int totalSum = 0;

            foreach (var kvp in _eventPools.OrderBy(kvp => kvp.Key.Name))
            {
                string name = kvp.Key.Name;
                int active = kvp.Value.CountActive;
                int inactive = kvp.Value.CountInactive;
                int total = kvp.Value.CountTotal;

                activeSum += active;
                inactiveSum += inactive;
                totalSum += total;

                sb.AppendLine($"{name}\t{active}\t{inactive}\t{total}");
            }

            sb.AppendLine();
            sb.AppendLine($"TOTAL\t{activeSum}\t{inactiveSum}\t{totalSum}");

            sb.AppendLine($"EventListPoolCount\t{_eventListPool.CountActive}\t{_eventListPool.CountInactive}\t{_eventListPool.CountTotal}");

            return sb.ToString();
        }

        private sealed class EventPool<T> : ObjectPool<T> where T: ScheduledEvent, new()
        {
            public EventPool() : base(ObjectPoolFlags.None) { }

            protected override T Allocate()
            {
                return new();
            }
        }

        private sealed class EventListPool : ObjectPool<LinkedList<ScheduledEvent>>
        {
            public EventListPool() : base(ObjectPoolFlags.None) { }

            protected override LinkedList<ScheduledEvent> Allocate()
            {
                return new();
            }
        }
    }
}
