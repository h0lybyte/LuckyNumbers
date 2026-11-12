using LuckyNumbers.Model;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// The universes (content/universes.txtpb, GameContent.Universes) and the one this run is in (UniverseState).
    /// Every scene that builds a system asks here for its generator, so the title, the station's orbit, the intro and
    /// the crash site all agree.
    /// </summary>
    public static class Multiverse
    {
        /// <summary>The universe with this id; the catalog's first (the default) for an empty or unknown id.</summary>
        public static Universe Find(string id)
        {
            var list = GameContent.Universes?.Universes;
            if (list == null || list.Count == 0) return null;
            if (!string.IsNullOrEmpty(id))
                foreach (var u in list)
                    if (u.Id == id) return u;
            return list[0];
        }

        /// <summary>The universe's system generator, else `fallback` (a scene's own backdrop generator).</summary>
        public static SystemGenerator Generator(string id, SystemGenerator fallback) => Find(id)?.Generator ?? fallback;

        /// <summary>Its worlds have holds (a surface gate near the crash site).</summary>
        public static bool HasHolds(string id) => Find(id)?.Holds ?? false;
    }
}
