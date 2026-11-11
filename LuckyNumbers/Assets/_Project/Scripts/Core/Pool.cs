using System;
using System.Collections.Generic;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// A free list of reusable things, agnostic of what they are: the owner says how to make one, what Get and
    /// Release do to it, and (for Unity objects a scene change can destroy) whether one is still alive; dead ones
    /// are skipped. The delegates are made once, so Get and Release allocate nothing. Not thread-safe: main thread.
    /// </summary>
    public sealed class Pool<T>
    {
        readonly Func<T> _create;
        readonly Action<T> _onGet, _onRelease;
        readonly Func<T, bool> _alive;
        readonly Stack<T> _free = new();

        /// <param name="create">A new one, in its released state.</param>
        /// <param name="onGet">Wake one up (after reuse or creation).</param>
        /// <param name="onRelease">Put one to sleep.</param>
        /// <param name="alive">False for one that was destroyed under us (default: always alive).</param>
        public Pool(Func<T> create, Action<T> onGet = null, Action<T> onRelease = null, Func<T, bool> alive = null)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _onGet = onGet;
            _onRelease = onRelease;
            _alive = alive;
        }

        /// <summary>How many this pool has made (out on loan or free).</summary>
        public int Created { get; private set; }
        public int Free => _free.Count;

        public T Get()
        {
            while (_free.Count > 0)
            {
                var x = _free.Pop();
                if (_alive != null && !_alive(x)) continue;
                _onGet?.Invoke(x);
                return x;
            }
            var made = _create();
            Created++;
            _onGet?.Invoke(made);
            return made;
        }

        public void Release(T x)
        {
            if (_alive != null && !_alive(x)) return;
            _onRelease?.Invoke(x);
            _free.Push(x);
        }

        /// <summary>Make enough up front that `count` exist in all (prewarm: no building mid-fight).</summary>
        public void Reserve(int count)
        {
            while (Created < count)
            {
                _free.Push(_create());
                Created++;
            }
        }

        /// <summary>Forget the free ones (their owner destroys them).</summary>
        public void Clear() => _free.Clear();
    }
}
