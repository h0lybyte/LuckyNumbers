using System;

namespace LuckyNumbers
{
    /// <summary>
    /// VContainer entry point (IStartable, ITickable, ...). The LuckyNumbers.Generators source generator
    /// registers it: call builder.Register&lt;YourAssembly&gt;() from the scope.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PresenterAttribute : Attribute { }

    /// <summary>
    /// Scene MonoBehaviour injected into presenters; registered with RegisterComponentInHierarchy.
    /// Views hold references and expose setters, no logic.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ViewAttribute : Attribute { }
}
