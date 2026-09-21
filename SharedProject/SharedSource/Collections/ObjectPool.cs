using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShadowCulling;

/// <summary>Abstraction for a pool that rents reusable instances and takes them back once the caller is done.</summary>
public interface IPool<T> where T : class
{
    /// <summary>Rents an instance from the pool, creating a new one when nothing is available.</summary>
    T Rent();

    /// <summary>Returns an instance to the pool so that it can be rented again later.</summary>
    void Return(T item);
}

/// <summary>An immutable snapshot of the counters of an <see cref="ObjectPool{T}"/>; cheap enough to poll.</summary>
[DebuggerDisplay("{ToString()}")]
public readonly struct PoolStatistics
{
    internal PoolStatistics(
        int sharedCount,
        int maxCapacity,
        int localCount,
        int maxPerThreadCapacity,
        long createdCount,
        long rentedCount,
        long reusedCount,
        long returnedCount,
        long discardedCount)
    {
        SharedCount = sharedCount;
        MaxCapacity = maxCapacity;
        LocalCount = localCount;
        MaxPerThreadCapacity = maxPerThreadCapacity;
        CreatedCount = createdCount;
        RentedCount = rentedCount;
        ReusedCount = reusedCount;
        ReturnedCount = returnedCount;
        DiscardedCount = discardedCount;
    }

    /// <summary>Gets the number of objects waiting in the shared pool, available to every thread.</summary>
    public int SharedCount { get; }

    /// <summary>Gets the maximum number of objects the shared pool is allowed to keep.</summary>
    public int MaxCapacity { get; }

    /// <summary>Gets the number of objects currently cached for the thread that read this snapshot.</summary>
    public int LocalCount { get; }

    /// <summary>Gets the maximum number of objects a single thread is allowed to cache.</summary>
    public int MaxPerThreadCapacity { get; }

    /// <summary>Gets the total number of objects created by the pool factory.</summary>
    public long CreatedCount { get; }

    /// <summary>Gets the total number of rent operations served.</summary>
    public long RentedCount { get; }

    /// <summary>Gets the number of rent operations that were served by reusing a pooled object.</summary>
    public long ReusedCount { get; }

    /// <summary>Gets the number of objects that were accepted back into the pool.</summary>
    public long ReturnedCount { get; }

    /// <summary>Gets the number of objects that were dropped because the pool was full or disposed.</summary>
    public long DiscardedCount { get; }

    /// <summary>Gets the ratio of rent operations that were served from the pool instead of the factory.</summary>
    public float HitRate => RentedCount == 0 ? 0f : (float)((double)ReusedCount / RentedCount);

    /// <inheritdoc/>
    public override string ToString() =>
        $"Rent={RentedCount}, Create={CreatedCount}, Reuse={ReusedCount}, Return={ReturnedCount}, " +
        $"Discard={DiscardedCount}, Hit={HitRate:P1}, Shared={SharedCount}/{MaxCapacity}, Local={LocalCount}/{MaxPerThreadCapacity}";
}

[DebuggerDisplay("Shared = {Count}/{Capacity}, Rented = {_rentedCount}, Created = {_createdCount}")]
/// <summary>
/// A generic, thread-aware pool that reduces GC pressure by reusing instances instead of allocating. Two tiers: a
/// per-thread LIFO cache and a shared queue that absorbs its overflow; anything that cannot be retained is dropped
/// through <c>onDiscard</c>.
/// </summary>
public sealed class ObjectPool<T> : IPool<T>, IDisposable where T : class
{
    /// <summary>Maximum number of pools that get a per-thread cache slot; the rest use the shared pool only.</summary>
    private const int MaxPoolSlots = 16;

    /// <summary>Number of per-thread cache slots handed out so far, one per pool instance.</summary>
    private static int s_slotCounter;

    /// <summary>Per-thread array of caches, indexed by pool slot.</summary>
    [ThreadStatic]
    private static ThreadLocalCache?[]? t_caches;

    private readonly Func<T> _factory;
    private readonly Action<T>? _onRent;
    private readonly Action<T>? _onReturn;
    private readonly Action<T>? _onDiscard;
    private readonly ConcurrentQueue<T> _sharedPool = new();
    private readonly int _maxCapacity;
    private readonly int _maxPerThreadCapacity;

    /// <summary>Index of this pool in the per-thread cache array, or <c>-1</c> when per-thread caching is disabled.</summary>
    private readonly int _slot;

    private readonly bool _drainOnThreadExit;

    private int _disposed;
    private int _sharedCount;
    private long _createdCount;
    private long _rentedCount;
    private long _reusedCount;
    private long _returnedCount;
    private long _discardedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObjectPool{T}"/> class. <paramref name="onReturn"/> resets an
    /// instance before it is stored, <paramref name="onDiscard"/> runs when one is dropped instead of retained (never
    /// on the finalizer path), and <c>maxPerThreadCapacity: 0</c> disables per-thread caching.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity argument is negative.</exception>
    public ObjectPool(
        Func<T> factory,
        Action<T>? onRent = null,
        Action<T>? onReturn = null,
        Action<T>? onDiscard = null,
        int maxCapacity = 1024,
        int maxPerThreadCapacity = 8,
        int initialCapacity = 0,
        bool drainOnThreadExit = true)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPerThreadCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);

        _onRent = onRent;
        _onReturn = onReturn;
        _onDiscard = onDiscard;
        _maxCapacity = maxCapacity;
        _maxPerThreadCapacity = maxPerThreadCapacity;
        _drainOnThreadExit = drainOnThreadExit;

        if (maxPerThreadCapacity > 0)
        {
            int slot = Interlocked.Increment(ref s_slotCounter) - 1;
            _slot = slot < MaxPoolSlots ? slot : -1;
        }
        else
        {
            _slot = -1;
        }

        if (initialCapacity > 0)
        {
            Prewarm(initialCapacity);
        }
    }

    /// <summary>Gets the number of objects waiting in the shared pool; thread caches are not included.</summary>
    public int Count => Volatile.Read(ref _sharedCount);

    /// <summary>Gets the maximum number of objects the shared pool is allowed to keep.</summary>
    public int Capacity => _maxCapacity;

    /// <summary>Gets the maximum number of objects a single thread is allowed to cache.</summary>
    public int MaxPerThreadCapacity => _maxPerThreadCapacity;

    /// <summary>Gets a value indicating whether the pool has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets a snapshot of the pool counters, suitable for performance logging.</summary>
    public PoolStatistics Statistics
    {
        get
        {
            int localCount = PeekCache()?.Count ?? 0;
            return new PoolStatistics(
                Volatile.Read(ref _sharedCount),
                _maxCapacity,
                localCount,
                _maxPerThreadCapacity,
                Interlocked.Read(ref _createdCount),
                Interlocked.Read(ref _rentedCount),
                Interlocked.Read(ref _reusedCount),
                Interlocked.Read(ref _returnedCount),
                Interlocked.Read(ref _discardedCount));
        }
    }

    /// <summary>Rents an instance: the calling thread's cache first, then the shared pool, then the factory.</summary>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Rent()
    {
        if (IsDisposed) { ThrowDisposed(); }

        T? item = PeekOrCreateCache()?.TryPop();

        if (item == null && _sharedPool.TryDequeue(out T? sharedItem))
        {
            Interlocked.Decrement(ref _sharedCount);
            item = sharedItem;
        }

        if (item == null)
        {
            item = Create();
        }
        else
        {
            Interlocked.Increment(ref _reusedCount);
        }

        Interlocked.Increment(ref _rentedCount);
        _onRent?.Invoke(item);
        return item;
    }

    /// <summary>Rents an instance wrapped in a lease that returns it on <see cref="PooledLease{T}.Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledLease<T> RentLease(out T item)
    {
        item = Rent();
        return new PooledLease<T>(this, item);
    }

    /// <summary>
    /// Returns an instance to the pool: reset through <c>onReturn</c>, then the thread cache, then the shared pool.
    /// Whatever neither can accept is dropped through <c>onDiscard</c>.
    /// </summary>
    public void Return(T item)
    {
        if (item is null) { return; }

        if (IsDisposed)
        {
            Discard(item);
            return;
        }

        _onReturn?.Invoke(item);

        ThreadLocalCache? cache = PeekOrCreateCache();
        if (cache != null && cache.TryPush(item))
        {
            Interlocked.Increment(ref _returnedCount);
            return;
        }

        if (!TryStoreShared(item))
        {
            Discard(item);
            return;
        }

        // Counted here and not inside TryStoreShared: objects that the finalizer of a thread cache moves into
        // the shared pool were already counted when they entered that cache.
        Interlocked.Increment(ref _returnedCount);
    }

    /// <summary>
    /// Creates <paramref name="count"/> instances up front and stores them in the shared pool, so that the first
    /// rents do not have to allocate. Clamped to the remaining shared pool capacity.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Prewarm(int count)
    {
        if (IsDisposed) { ThrowDisposed(); }
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (int i = 0; i < count; i++)
        {
            T item = Create();
            _onReturn?.Invoke(item);

            if (!TryStoreShared(item))
            {
                Discard(item);
                break;
            }
        }
    }

    /// <summary>Drops every object the pool holds, through <c>onDiscard</c>. The pool stays usable afterwards.</summary>
    public void Clear()
    {
        DrainCache(PeekCache());
        DrainSharedPool();
    }

    /// <summary>
    /// Drops every object the pool holds and marks it disposed: renting throws, returning silently drops.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }

        // Detach the calling thread's cache so it cannot be reused, then release what it holds.
        ThreadLocalCache? cache = PeekCache();
        if (cache != null && _slot >= 0)
        {
            t_caches![_slot] = null;
        }

        DrainCache(cache);
        DrainSharedPool();
    }

    /// <summary>
    /// Called from the finalizer of a <see cref="ThreadLocalCache"/> once its thread has terminated: what it still
    /// cached goes back to the shared pool, or is dropped when the pool is full or gone.
    /// </summary>
    private void DrainThreadLocalCache(ThreadLocalCache cache)
    {
        while (true)
        {
            T? item = cache.TryPop();
            if (item == null) { break; }

            if (IsDisposed || !TryStoreShared(item))
            {
                Interlocked.Increment(ref _discardedCount);
            }
        }
    }

    private void DrainCache(ThreadLocalCache? cache)
    {
        if (cache == null) { return; }

        while (true)
        {
            T? item = cache.TryPop();
            if (item == null) { break; }
            Discard(item);
        }
    }

    private void DrainSharedPool()
    {
        while (_sharedPool.TryDequeue(out T? item))
        {
            Interlocked.Decrement(ref _sharedCount);
            Discard(item);
        }
    }

    /// <summary>Tries to store an object in the shared pool within its capacity limit. Also runs on the finalizer path,
    /// where no user callback may be invoked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryStoreShared(T item)
    {
        if (Interlocked.Increment(ref _sharedCount) <= _maxCapacity)
        {
            _sharedPool.Enqueue(item);
            return true;
        }

        Interlocked.Decrement(ref _sharedCount);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T Create()
    {
        T item = _factory();
        if (item == null)
        {
            throw new InvalidOperationException(
                $"The factory of pool '{nameof(ObjectPool<T>)}<{typeof(T)}>' returned null.");
        }

        Interlocked.Increment(ref _createdCount);
        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Discard(T item)
    {
        Interlocked.Increment(ref _discardedCount);
        _onDiscard?.Invoke(item);
    }

    /// <summary>Gets the calling thread's cache without creating one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ThreadLocalCache? PeekCache()
    {
        int slot = _slot;
        if (slot < 0) { return null; }

        ThreadLocalCache?[]? caches = t_caches;
        return caches?[slot];
    }

    /// <summary>Gets the calling thread's cache, creating it on first use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ThreadLocalCache? PeekOrCreateCache()
    {
        int slot = _slot;
        if (slot < 0) { return null; }

        ThreadLocalCache?[]? caches = t_caches;
        if (caches == null)
        {
            caches = t_caches = new ThreadLocalCache?[MaxPoolSlots];
        }

        ThreadLocalCache? cache = caches[slot];
        if (cache == null)
        {
            cache = new ThreadLocalCache(this, _maxPerThreadCapacity, _drainOnThreadExit);
            caches[slot] = cache;
        }

        return cache;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(ObjectPool<T>));

    /// <summary>A fixed-size LIFO cache owned by a single thread; finalizable so its objects survive the thread.</summary>
    private sealed class ThreadLocalCache
    {
        private readonly ObjectPool<T> _owner;
        private readonly T?[] _items;
        private readonly bool _drainOnThreadExit;
        private int _count;

        internal ThreadLocalCache(ObjectPool<T> owner, int capacity, bool drainOnThreadExit)
        {
            _owner = owner;
            _items = new T?[capacity];
            _drainOnThreadExit = drainOnThreadExit;

            if (!drainOnThreadExit)
            {
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>Gets the number of objects currently cached.</summary>
        internal int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T? TryPop()
        {
            int index = _count - 1;
            if (index < 0) { return null; }

            T? item = _items[index];
            _items[index] = null;
            _count = index;
            return item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryPush(T item)
        {
            int index = _count;
            if (index >= _items.Length) { return false; }

            _items[index] = item;
            _count = index + 1;
            return true;
        }

        ~ThreadLocalCache()
        {
            if (!_drainOnThreadExit) { return; }

            try
            {
                _owner.DrainThreadLocalCache(this);
            }
            catch
            {
                // A pool must never be able to take down the finalizer thread.
            }
        }
    }
}
