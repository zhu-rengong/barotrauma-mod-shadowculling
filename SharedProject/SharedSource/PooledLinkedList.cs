using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace ShadowCulling
{
    [DebuggerDisplay("Value = {_item}")]
    public sealed class PooledLinkedListNode<T>
    {
        internal PooledLinkedList<T>? _list;
        internal PooledLinkedListNode<T>? _next;
        internal PooledLinkedListNode<T>? _prev;
        internal T _item = default!;

        internal PooledLinkedListNode(PooledLinkedList<T> list, T value = default!)
        {
            _list = list;
            _item = value;
        }

        public PooledLinkedList<T>? List => _list;

        public PooledLinkedListNode<T>? Next => _next == null || _next == _list!._head ? null : _next;

        public PooledLinkedListNode<T>? Previous => _prev == null || this == _list!._head ? null : _prev;

        public T Value
        {
            get => _item;
            set => _item = value;
        }

        public ref T ValueRef => ref _item;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Invalidate()
        {
            _list = null;
            _next = null;
            _prev = null;
        }
    }

    [DebuggerDisplay("Count = {Count}, FreeCount = {FreeCount}")]
    public sealed class PooledLinkedList<T> : ICollection<T>, IReadOnlyCollection<T>
    {
        internal PooledLinkedListNode<T>? _head;
        internal int _count;
        internal int _version;

        private PooledLinkedListNode<T>? _freeList;
        private int _freeCount;

        private static readonly EqualityComparer<T> s_comparer = EqualityComparer<T>.Default;

        public PooledLinkedList() { }

        public PooledLinkedList(int initialCapacity)
        {
            if (initialCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            }

            for (int i = 0; i < initialCapacity; i++)
            {
                ReturnNode(new(this));
            }
        }

        public int Count => _count;

        /// <summary>Gets the number of nodes currently available in the internal free pool.</summary>
        public int FreeCount => _freeCount;

        public PooledLinkedListNode<T>? First => _head;
        public PooledLinkedListNode<T>? Last => _head?._prev;

        bool ICollection<T>.IsReadOnly => false;
        void ICollection<T>.Add(T value) => AddLast(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private PooledLinkedListNode<T> RentNode()
        {
            if (_freeList != null)
            {
                var node = _freeList;
                _freeList = node._next;
                _freeCount--;
                node._list = this;
                node._next = null;
                return node;
            }
            return new(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReturnNode(PooledLinkedListNode<T> node)
        {
            node._next = _freeList;
            _freeList = node;
            _freeCount++;
        }

        public PooledLinkedListNode<T> AddFirst(in T value)
        {
            var node = RentNode();
            node._item = value;

            if (_head == null)
            {
                InternalInsertNodeToEmptyList(node);
            }
            else
            {
                InternalInsertNodeBefore(_head, node);
                _head = node;
            }

            return node;
        }

        public void AddFirst(PooledLinkedListNode<T> node)
        {
            ValidateNewNode(node);

            if (_head == null)
            {
                InternalInsertNodeToEmptyList(node);
            }
            else
            {
                InternalInsertNodeBefore(_head, node);
                _head = node;
            }

            node._list = this;
        }

        public PooledLinkedListNode<T> AddLast(in T value)
        {
            var result = RentNode();
            result._item = value;

            if (_head == null)
            {
                InternalInsertNodeToEmptyList(result);
            }
            else
            {
                InternalInsertNodeBefore(_head, result);
            }

            return result;
        }

        public void AddLast(PooledLinkedListNode<T> node)
        {
            ValidateNewNode(node);

            if (_head == null)
            {
                InternalInsertNodeToEmptyList(node);
            }
            else
            {
                InternalInsertNodeBefore(_head, node);
            }

            node._list = this;
        }

        public PooledLinkedListNode<T> AddAfter(PooledLinkedListNode<T> node, in T value)
        {
            ValidateNode(node);
            var result = RentNode();
            result._item = value;
            InternalInsertNodeBefore(node._next!, result);
            return result;
        }

        public void AddAfter(PooledLinkedListNode<T> node, PooledLinkedListNode<T> newNode)
        {
            ValidateNode(node);
            ValidateNewNode(newNode);
            InternalInsertNodeBefore(node._next!, newNode);
            newNode._list = this;
        }

        public PooledLinkedListNode<T> AddBefore(PooledLinkedListNode<T> node, in T value)
        {
            ValidateNode(node);
            var result = RentNode();
            result._item = value;
            InternalInsertNodeBefore(node, result);
            if (node == _head)
            {
                _head = result;
            }
            return result;
        }

        public void AddBefore(PooledLinkedListNode<T> node, PooledLinkedListNode<T> newNode)
        {
            ValidateNode(node);
            ValidateNewNode(newNode);
            InternalInsertNodeBefore(node, newNode);
            newNode._list = this;
            if (node == _head)
            {
                _head = newNode;
            }
        }

        public void Clear() => Clear(returnNode: false);

        public void Clear(bool returnNode = false)
        {
            var current = _head;
            while (current != null)
            {
                var temp = current;
                current = current.Next;
                temp.Invalidate();
                if (returnNode)
                {
                    ReturnNode(temp);
                }
            }

            _head = null;
            _count = 0;
            _version++;
        }

        public bool Contains(T value) => Find(value) != null;

        public void CopyTo(T[] array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index exceeds array length.");
            }
            if (array.Length - index < _count)
            {
                throw new ArgumentException("Insufficient space in target array.");
            }

            var node = _head;
            if (node != null)
            {
                do
                {
                    array[index++] = node!._item;
                    node = node._next;
                } while (node != _head);
            }
        }

        public PooledLinkedListNode<T>? Find(in T value)
        {
            var node = _head;
            EqualityComparer<T> c = EqualityComparer<T>.Default;
            if (node != null)
            {
                if (value != null)
                {
                    do
                    {
                        if (c.Equals(node!._item, value))
                        {
                            return node;
                        }
                        node = node._next;
                    } while (node != _head);
                }
                else
                {
                    do
                    {
                        if (node!._item == null)
                        {
                            return node;
                        }
                        node = node._next;
                    } while (node != _head);
                }
            }
            return null;
        }

        public PooledLinkedListNode<T>? FindLast(T value)
        {
            if (_head == null)
            {
                return null;
            }

            var last = _head._prev;
            var node = last;
            EqualityComparer<T> c = EqualityComparer<T>.Default;
            if (node != null)
            {
                if (value != null)
                {
                    do
                    {
                        if (c.Equals(node!._item, value))
                        {
                            return node;
                        }

                        node = node._prev;
                    } while (node != last);
                }
                else
                {
                    do
                    {
                        if (node!._item == null)
                        {
                            return node;
                        }
                        node = node._prev;
                    } while (node != last);
                }
            }
            return null;
        }

        public bool Remove(T value) => Remove(value, returnNode: false);

        public bool Remove(in T value, bool returnNode = false)
        {
            var node = Find(value);
            if (node != null)
            {
                InternalRemoveNode(node, returnNode);
                return true;
            }
            return false;
        }

        public void Remove(PooledLinkedListNode<T> node, bool returnNode = false)
        {
            ValidateNode(node);
            InternalRemoveNode(node, returnNode);
        }

        public void RemoveFirst(bool returnNode = false)
        {
            if (_head == null) { throw new InvalidOperationException("The list is empty."); }
            InternalRemoveNode(_head, returnNode);
        }

        public void RemoveLast(bool returnNode = false)
        {
            if (_head == null) { throw new InvalidOperationException("The list is empty."); }
            InternalRemoveNode(_head._prev!, returnNode);
        }

        private void InternalInsertNodeBefore(PooledLinkedListNode<T> node, PooledLinkedListNode<T> newNode)
        {
            newNode._next = node;
            newNode._prev = node._prev;
            node._prev!._next = newNode;
            node._prev = newNode;
            _version++;
            _count++;
        }

        private void InternalInsertNodeToEmptyList(PooledLinkedListNode<T> newNode)
        {
            Debug.Assert(_head == null && _count == 0);
            newNode._next = newNode;
            newNode._prev = newNode;
            _head = newNode;
            _version++;
            _count++;
        }

        internal void InternalRemoveNode(PooledLinkedListNode<T> node, bool returnNode = false)
        {
            Debug.Assert(node._list == this, "Deleting the node from another list!");
            Debug.Assert(_head != null, "This method shouldn't be called on empty list!");
            if (node._next == node)
            {
                Debug.Assert(_count == 1 && _head == node, "this should only be true for a list with only one node");
                _head = null;
            }
            else
            {
                node._next!._prev = node._prev;
                node._prev!._next = node._next;
                if (_head == node)
                {
                    _head = node._next;
                }
            }
            node.Invalidate();
            if (returnNode)
            {
                ReturnNode(node);
            }
            _count--;
            _version++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ValidateNewNode(PooledLinkedListNode<T> node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (node._list != null)
            {
                throw new InvalidOperationException("Node is already attached to another list.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateNode(PooledLinkedListNode<T> node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (node._list != this)
            {
                throw new InvalidOperationException("Node does not belong to this list.");
            }
        }

        public Enumerator GetEnumerator() => new(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

        public struct Enumerator : IEnumerator<T>, IEnumerator
        {
            private readonly PooledLinkedList<T> _list;
            private PooledLinkedListNode<T>? _node;
            private readonly int _version;
            private T? _current;
            private int _index;

            internal Enumerator(PooledLinkedList<T> list)
            {
                _list = list;
                _version = list._version;
                _node = list._head;
                _current = default;
                _index = 0;
            }

            public T Current => _current!;

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _list.Count + 1))
                    {
                        throw new InvalidOperationException("Enumeration operation cannot happen.");
                    }

                    return Current;
                }
            }

            public bool MoveNext()
            {
                if (_version != _list._version)
                {
                    throw new InvalidOperationException("Collection was modified after the enumerator was created.");
                }

                if (_node == null)
                {
                    _index = _list.Count + 1;
                    return false;
                }

                ++_index;
                _current = _node._item;
                _node = _node._next;
                if (_node == _list._head)
                {
                    _node = null;
                }
                return true;
            }

            void IEnumerator.Reset()
            {
                if (_version != _list._version)
                {
                    throw new InvalidOperationException("Collection was modified after the enumerator was created.");
                }

                _current = default;
                _node = _list._head;
                _index = 0;
            }

            public void Dispose()
            {
            }
        }
    }
}