using System.Collections.Concurrent;

namespace ShadowCulling
{
    /// <summary>
    /// A generic object pool for reusing objects to reduce garbage collection overhead.
    /// </summary>
    /// <typeparam name="T">The type of objects to pool. Must be a reference type.</typeparam>
    public class ObjectPool<T> where T : class
    {
        private readonly ConcurrentQueue<T> _objectPool;
        private readonly int _maxCapacity;
        private readonly Func<T> _objectFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectPool{T}"/> class.
        /// </summary>
        /// <param name="objectFactory">The function used to create new instances of <typeparamref name="T"/>.</param>
        /// <param name="maxCapacity">The maximum number of objects to keep in the pool.</param>
        public ObjectPool(Func<T> objectFactory, int maxCapacity = 1024)
        {
            _objectPool = new ConcurrentQueue<T>();
            _maxCapacity = maxCapacity;
            _objectFactory = objectFactory ?? throw new ArgumentNullException(nameof(objectFactory));
        }

        /// <summary>
        /// Gets an object from the pool or creates a new one if the pool is empty.
        /// </summary>
        /// <returns>An instance of <typeparamref name="T"/>.</returns>
        public T Get()
        {
            if (_objectPool.TryDequeue(out T? pooledObject))
            {
                return pooledObject!;
            }
            return _objectFactory();
        }

        /// <summary>
        /// Returns an object to the pool for reuse.
        /// </summary>
        /// <param name="objectToReturn">The object to return to the pool.</param>
        public void Return(T objectToReturn)
        {
            if (objectToReturn == null)
            {
                return;
            }

            if (_objectPool.Count < _maxCapacity)
            {
                _objectPool.Enqueue(objectToReturn);
            }
        }

        /// <summary>
        /// Gets the current number of objects in the pool.
        /// </summary>
        public int Count => _objectPool.Count;

        /// <summary>
        /// Gets the maximum capacity of the pool.
        /// </summary>
        public int Capacity => _maxCapacity;
    }
}
