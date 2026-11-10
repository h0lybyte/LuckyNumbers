namespace LuckyNumbers.Core
{
    /// <summary>A job's planet site: its seed (terrain, wreck, cargo) is the universe's, varied by the job's.</summary>
    public static class JobSite
    {
        public static uint Seed(uint universe, uint job) => job == 0 ? universe : (universe ^ job) | 1u;
    }
}
