using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;

namespace Whosyouradddy.ShadowCulling
{
    public class ObjectPool<T> where T : class
    {
        private readonly ConcurrentQueue<T> _pool;
        private readonly int _capacity;
        private Func<T> _objectCreator;

        public ObjectPool(Func<T> objectCreator, int capacity = 1024)
        {
            _pool = new();
            _capacity = capacity;
            _objectCreator = objectCreator;
        }

        public T Get()
        {
            if (_pool.TryDequeue(out T? @object))
            {
                return @object!;
            }
            return _objectCreator();
        }

        public void Return(T @object)
        {
            if (@object == null) return;

            if (_pool.Count < _capacity)
            {
                _pool.Enqueue(@object);
            }
        }

        public int Count => _pool.Count;
        public int Capacity => _capacity;
    }
}
