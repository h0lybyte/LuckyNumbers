using LuckyNumbers.Model;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Dev shooting range scope; parent is RootLifetimeScope. First-person control with the planet's walk feel (no
    /// crash tremor), the crash site's overlay (hint, crosshair) and RangePresenter. Built by SceneBuilder.BuildRange.
    /// </summary>
    public sealed class RangeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            var walk = GameContent.Planet.Clone();
            walk.CrashTremorDegrees = 0f;
            builder.RegisterInstance(walk);
            builder.RegisterEntryPoint<FirstPersonPresenter>().AsSelf().As<IFirstPerson>();
            builder.RegisterComponentInHierarchy<PlayerView>();
            builder.RegisterComponentInHierarchy<ArmsView>();
            builder.RegisterComponentInHierarchy<PlanetView>();
            builder.RegisterEntryPoint<RangePresenter>();
        }
    }
}
