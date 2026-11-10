using System.Collections.Generic;
using LuckyNumbers.Model;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// What a station is built from: the plan and everything placed by plan coordinates (room names, props,
    /// consoles, the dock) and the room the call comes in. From content (the hand-drawn plan) or from
    /// <see cref="LayoutGenerator"/> (seeded); the page's layout editor swaps just the plan.
    /// </summary>
    public sealed class StationLayout
    {
        public readonly List<string> Plan = new();
        public readonly List<StationRoom> Rooms = new();
        public readonly List<StationProp> Props = new();
        public readonly List<StationSwitch> Switches = new();
        public StationDock Dock;
        public string CallRoom = "";
        /// <summary>Seed it was generated from (0: content).</summary>
        public uint Seed;

        public static StationLayout FromContent(StationScene config)
        {
            var layout = new StationLayout { Dock = config.Dock, CallRoom = config.Call?.Room ?? "" };
            layout.Plan.AddRange(config.Plan);
            layout.Rooms.AddRange(config.Rooms);
            layout.Props.AddRange(config.Props);
            layout.Switches.AddRange(config.Switches);
            return layout;
        }

        /// <summary>The same layout over a different plan (the layout editor).</summary>
        public StationLayout WithPlan(IEnumerable<string> rows)
        {
            var layout = new StationLayout { Dock = Dock, CallRoom = CallRoom, Seed = Seed };
            layout.Plan.AddRange(rows);
            layout.Rooms.AddRange(Rooms);
            layout.Props.AddRange(Props);
            layout.Switches.AddRange(Switches);
            return layout;
        }
    }
}
