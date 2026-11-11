using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The Flight School's scope (parent: RootLifetimeScope): the crash site's overlay and ships (PlanetView: your
    /// fighter, the freighter, the flight camera) flown by FlightSchoolPresenter. Built by SceneBuilder.BuildFlightSchool.
    /// </summary>
    public sealed class FlightSchoolScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            builder.RegisterComponentInHierarchy<PlanetView>();
            builder.RegisterEntryPoint<FlightSchoolPresenter>();
        }
    }
}
