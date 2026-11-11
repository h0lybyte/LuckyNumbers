using LuckyNumbers.Model;
using MessagePipe;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Spikes.LibsSmokeTest
{
    /// <summary>Constructor marked [Inject] (VContainer's way to keep it under IL2CPP stripping).</summary>
    public sealed class InjectedService
    {
        readonly IPublisher<SceneChangeRequested> _publisher;

        [Inject]
        public InjectedService(IPublisher<SceneChangeRequested> publisher) => _publisher = publisher;

        public void Request(GameScene scene) => _publisher.Publish(new SceneChangeRequested(scene));
    }

    /// <summary>Constructor without any attribute: probes whether stripping removes it (own container, see LibsSmokeTest).</summary>
    public sealed class PlainService
    {
        public readonly int Answer;
        public PlainService() => Answer = 42;
    }

    /// <summary>Entry point driven by VContainer's PlayerLoop integration.</summary>
    public sealed class StartProbe : IStartable
    {
        public static bool Started;

        [Inject]   // an implicit constructor is stripped too (Medium), and the scope's build fails without one
        public StartProbe() { }

        public void Start() => Started = true;
    }

    public static class VContainerProbe
    {
        public static LifetimeScope CreateScope() => LifetimeScope.Create(builder =>
        {
            var options = builder.RegisterMessagePipe();
            builder.RegisterModelEvents(options); // generated from (ln.event) messages
            builder.RegisterModelState();         // generated from (ln.state) messages
            builder.Register<InjectedService>(Lifetime.Singleton);
            builder.RegisterEntryPoint<StartProbe>();
        }, "SmokeTestScope");
    }
}
