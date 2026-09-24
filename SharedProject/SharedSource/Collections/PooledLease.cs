namespace ShadowCulling;

/// <summary>
/// A disposable handle over an object rented from an <see cref="IPool{T}"/>; disposing returns it to its pool. A
/// mutable value type owning the rented object, so do not copy it or store it long-term.
/// </summary>
public struct PooledLease<T> : IDisposable where T : class
{
    private IPool<T>? _pool;
    private T? _item;

    internal PooledLease(IPool<T> pool, T item)
    {
        _pool = pool;
        _item = item;
    }

    /// <summary>Gets the rented object.</summary>
    /// <exception cref="InvalidOperationException">The lease has already been disposed.</exception>
    public T Value => _item ?? throw new InvalidOperationException("The lease has already been returned to the pool.");

    /// <summary>Gets a value indicating whether the lease still owns an object.</summary>
    public bool IsValid => _item != null;

    /// <summary>Returns the rented object to its pool; does nothing when the lease was already disposed.</summary>
    public void Dispose()
    {
        IPool<T>? pool = _pool;
        T? item = _item;
        if (pool == null || item == null) { return; }

        // Clears the state first so that a throwing hook cannot cause a double return.
        _pool = null;
        _item = null;
        pool.Return(item);
    }
}
