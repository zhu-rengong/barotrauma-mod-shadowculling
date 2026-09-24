using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ShadowCulling;

/// <summary>
/// A 4-byte handle to a node of a <see cref="PooledLinkedList{T}"/>, valid only for the list that produced it.
/// </summary>
[DebuggerDisplay("Index = {Index}")]
public readonly struct PooledLinkedListNode : IEquatable<PooledLinkedListNode>
{
    /// <summary>The slot index that means "no node".</summary>
    internal const int NoneIndex = -1;

    /// <summary>The slot this handle refers to, or <see cref="NoneIndex"/> when it refers to nothing.</summary>
    private readonly int _index;

    internal PooledLinkedListNode(int slot) => _index = slot;

    /// <summary>Gets the empty handle. Use it instead of <c>default</c>, which refers to slot 0.</summary>
    public static PooledLinkedListNode None => new(NoneIndex);

    /// <summary>Gets the index of the slot this handle refers to, or <c>-1</c> when the handle is empty.</summary>
    public int Index => _index;

    /// <summary>Gets a value indicating whether this handle refers to a node.</summary>
    public bool IsValid => _index >= 0;

    /// <summary>Gets a value indicating whether this handle refers to no node.</summary>
    public bool IsNull => _index < 0;

    /// <inheritdoc/>
    public bool Equals(PooledLinkedListNode other) => _index == other._index;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PooledLinkedListNode other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _index;

    /// <inheritdoc/>
    public override string ToString() => IsValid ? $"Node({_index})" : nameof(None);

    /// <summary>Compares two handles for equality.</summary>
    public static bool operator ==(PooledLinkedListNode left, PooledLinkedListNode right) => left._index == right._index;

    /// <summary>Compares two handles for inequality.</summary>
    public static bool operator !=(PooledLinkedListNode left, PooledLinkedListNode right) => left._index != right._index;
}

/// <summary>
/// A doubly linked list whose nodes are recycled through an internal free list instead of being allocated, for hot
/// paths that rebuild the same small list every frame. Nodes live in the list's own arrays behind a 4-byte handle,
/// so a list at steady size stops allocating; not thread-safe.
/// </summary>
[DebuggerDisplay("Count = {Count}, Free = {FreeCount}, Limbo = {LimboCount}, Capacity = {Capacity}")]
public sealed class PooledLinkedList<T> : IReadOnlyCollection<T>, IEnumerable<T>
{
    /// <summary>Initial size of the backing array of a list that grows on demand.</summary>
    private const int DefaultCapacity = 4;

    /// <summary>Index used for "no link" in the link arrays.</summary>
    private const int NoLink = PooledLinkedListNode.NoneIndex;

    /// <summary><see cref="_prev"/> marker distinguishing a free slot from a detached one (debug assertions only).</summary>
    private const int FreeMarker = -2;

    /// <summary>Payloads, indexed by slot.</summary>
    private T[] _items;

    /// <summary>Next slot of each node in the ring, or the next free slot while the slot is on the free list.</summary>
    private int[] _next;

    /// <summary>Previous slot of each node in the ring; see <see cref="FreeMarker"/> and <see cref="NoLink"/>.</summary>
    private int[] _prev;

    /// <summary>Index of the head of the ring, or <see cref="NoLink"/> when the list is empty.</summary>
    private int _head;

    /// <summary>Number of live nodes in the ring.</summary>
    private int _count;

    /// <summary>Index of the most recently recycled slot, or <see cref="NoLink"/> when the free list is empty.</summary>
    private int _freeHead;

    /// <summary>Number of slots on the free list.</summary>
    private int _freeCount;

    /// <summary>Incremented on every structural change so that enumerators can detect concurrent modification.</summary>
    private int _version;

    /// <summary>Initializes an empty list whose backing arrays start empty and grow on demand.</summary>
    public PooledLinkedList()
    {
        _items = Array.Empty<T>();
        _next = Array.Empty<int>();
        _prev = Array.Empty<int>();
        _head = NoLink;
        _freeHead = NoLink;
    }

    /// <summary>Initializes an empty list with room for <paramref name="initialCapacity"/> nodes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is negative.</exception>
    public PooledLinkedList(int initialCapacity)
        : this()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (initialCapacity > 0)
        {
            // Resize() chains the new slots onto the free list, which is exactly the state an empty list needs.
            Resize(initialCapacity);
        }
    }

    /// <summary>Gets the number of live nodes in the list.</summary>
    public int Count => _count;

    /// <summary>Gets the number of slots waiting on the free list, ready to be rented by the next insertion.</summary>
    public int FreeCount => _freeCount;

    /// <summary>Gets the number of detached nodes, neither re-attached nor recycled; zero in steady state.</summary>
    public int LimboCount => _next.Length - _count - _freeCount;

    /// <summary>Gets the number of node slots the backing arrays can hold without growing.</summary>
    public int Capacity => _next.Length;

    /// <summary>Gets the handle of the first node, or <see cref="PooledLinkedListNode.None"/> when the list is empty.</summary>
    public PooledLinkedListNode First => _head == NoLink ? PooledLinkedListNode.None : new PooledLinkedListNode(_head);

    /// <summary>Gets the handle of the last node, or <see cref="PooledLinkedListNode.None"/> when the list is empty.</summary>
    public PooledLinkedListNode Last =>
        _head == NoLink ? PooledLinkedListNode.None : new PooledLinkedListNode(_prev[_head]);

    /// <summary>Makes sure the backing array can hold <paramref name="capacity"/> nodes without growing.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > _next.Length)
        {
            Resize(capacity);
        }
    }

    /// <summary>Inserts a new node at the beginning of the list, returning its handle.</summary>
    public PooledLinkedListNode AddFirst(in T value)
    {
        int slot = RentSlot(value);
        InsertFirst(slot);
        return new PooledLinkedListNode(slot);
    }

    /// <summary>Inserts a new node at the end of the list, returning its handle.</summary>
    public PooledLinkedListNode AddLast(in T value)
    {
        int slot = RentSlot(value);
        InsertLast(slot);
        return new PooledLinkedListNode(slot);
    }

    /// <summary>Inserts a new node immediately before the live <paramref name="node"/>, returning its handle.</summary>
    public PooledLinkedListNode AddBefore(PooledLinkedListNode node, in T value)
    {
        int anchor = node.Index;
        ValidateLive(anchor);

        int slot = RentSlot(value);
        InsertBeforeAnchor(anchor, slot);
        return new PooledLinkedListNode(slot);
    }

    /// <summary>Inserts a new node immediately after the live <paramref name="node"/>, returning its handle.</summary>
    public PooledLinkedListNode AddAfter(PooledLinkedListNode node, in T value)
    {
        int anchor = node.Index;
        ValidateLive(anchor);

        int slot = RentSlot(value);
        // The next index is read after renting: renting may have grown - and thus reallocated - the arrays.
        InsertBefore(_next[anchor], slot);
        return new PooledLinkedListNode(slot);
    }

    /// <summary>Attaches a detached node at the beginning of the list.</summary>
    public void AttachFirst(PooledLinkedListNode node) => InsertFirst(DetachedSlot(node));

    /// <summary>Attaches a detached node at the end of the list.</summary>
    public void AttachLast(PooledLinkedListNode node) => InsertLast(DetachedSlot(node));

    /// <summary>Attaches a detached node immediately before the live <paramref name="node"/>.</summary>
    public void AttachBefore(PooledLinkedListNode node, PooledLinkedListNode detached)
    {
        int anchor = node.Index;
        ValidateLive(anchor);

        InsertBeforeAnchor(anchor, DetachedSlot(detached));
    }

    /// <summary>Attaches a detached node immediately after the live <paramref name="node"/>.</summary>
    public void AttachAfter(PooledLinkedListNode node, PooledLinkedListNode detached)
    {
        int anchor = node.Index;
        ValidateLive(anchor);

        InsertBefore(_next[anchor], DetachedSlot(detached));
    }

    /// <summary>
    /// Takes a live node out of the list without recycling its slot, so it keeps its value. The handle is out of the
    /// list until it is attached again or handed back with <see cref="Recycle"/>.
    /// </summary>
    public void Detach(PooledLinkedListNode node)
    {
        int slot = node.Index;
        ValidateLive(slot);

        if (_count == 1)
        {
            _head = NoLink;
        }
        else
        {
            int next = _next[slot];
            int previous = _prev[slot];
            _prev[next] = previous;
            _next[previous] = next;

            if (_head == slot)
            {
                _head = next;
            }
        }

        // Cleared last: it is what marks the slot as detached instead of live or free.
        _next[slot] = NoLink;
        _prev[slot] = NoLink;

        _count--;
        _version++;
    }

    /// <summary>
    /// Hands a detached node back to the free list so the next insertion can reuse its slot. Only valid for a node
    /// taken out with <see cref="Detach"/>; use <see cref="Remove"/> for a live one.
    /// </summary>
    public void Recycle(PooledLinkedListNode node)
    {
        int slot = node.Index;
        Debug.Assert(IsDetachedSlot(slot), "Recycle expects a node that was taken out with Detach.");
        RecycleSlot(slot);
        _version++;
    }

    /// <summary>Removes a live node from the list and hands its slot back to the free list.</summary>
    public void Remove(PooledLinkedListNode node)
    {
        Detach(node);
        RecycleSlot(node.Index);
    }

    /// <summary>Removes the first node and hands its slot back to the free list.</summary>
    /// <exception cref="InvalidOperationException">The list is empty.</exception>
    public void RemoveFirst()
    {
        if (_head == NoLink)
        {
            ThrowEmpty();
        }

        Remove(new PooledLinkedListNode(_head));
    }

    /// <summary>Removes the last node and hands its slot back to the free list.</summary>
    /// <exception cref="InvalidOperationException">The list is empty.</exception>
    public void RemoveLast()
    {
        if (_head == NoLink)
        {
            ThrowEmpty();
        }

        Remove(new PooledLinkedListNode(_prev[_head]));
    }

    /// <summary>Removes the first node, if there is one; returns whether one was removed.</summary>
    public bool TryRemoveFirst()
    {
        if (_head == NoLink)
        {
            return false;
        }

        Remove(new PooledLinkedListNode(_head));
        return true;
    }

    /// <summary>Removes the last node, if there is one; returns whether one was removed.</summary>
    public bool TryRemoveLast()
    {
        if (_head == NoLink)
        {
            return false;
        }

        Remove(new PooledLinkedListNode(_prev[_head]));
        return true;
    }

    /// <summary>
    /// Empties the list, handing every slot back to the free list; the backing array is kept. Nodes left in limbo by
    /// an interrupted detach/attach sequence are recovered by a slow path.
    /// </summary>
    public void Clear()
    {
        int head = _head;
        int remaining = _count;

        _head = NoLink;
        _count = 0;

        while (remaining-- > 0)
        {
            int next = _next[head];
            RecycleSlot(head);
            head = next;
        }

        if (_freeCount != _next.Length)
        {
            RebuildFreeList();
        }

        _version++;
    }

    /// <summary>Gets the handle of the node following the live <paramref name="node"/>, or <c>None</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledLinkedListNode Next(PooledLinkedListNode node)
    {
        int slot = node.Index;
        ValidateLive(slot);

        int next = _next[slot];
        return next == _head ? PooledLinkedListNode.None : new PooledLinkedListNode(next);
    }

    /// <summary>Gets the handle of the node preceding the live <paramref name="node"/>, or <c>None</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledLinkedListNode Previous(PooledLinkedListNode node)
    {
        int slot = node.Index;
        ValidateLive(slot);

        return slot == _head ? PooledLinkedListNode.None : new PooledLinkedListNode(_prev[slot]);
    }

    /// <summary>Gets or sets the value stored in a live node.</summary>
    public T this[PooledLinkedListNode node]
    {
        get
        {
            int slot = node.Index;
            ValidateLive(slot);
            return _items[slot];
        }
        set
        {
            int slot = node.Index;
            ValidateLive(slot);
            _items[slot] = value;
        }
    }

    /// <summary>
    /// Gets a reference to the value stored in a live node, avoiding a copy out of the array. Any insertion that
    /// grows the array invalidates it, so read it before inserting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T ValueRef(PooledLinkedListNode node)
    {
        int slot = node.Index;
        ValidateLive(slot);
        return ref _items[slot];
    }

    /// <summary>Returns a struct enumerator over the live nodes, in order; enumerating while the list is structurally
    /// modified throws.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowEmpty() => throw new InvalidOperationException("The list is empty.");

    /// <summary>Grows the backing arrays to hold exactly <paramref name="capacity"/> slots.</summary>
    private void Resize(int capacity)
    {
        int oldCapacity = _next.Length;
        if (capacity <= oldCapacity)
        {
            return;
        }

        Array.Resize(ref _items, capacity);
        Array.Resize(ref _next, capacity);
        Array.Resize(ref _prev, capacity);

        // Chain the new slots onto the free list in ascending order, so the lowest - and therefore most cache
        // friendly - index is handed out first. Existing nodes keep their index and thus their handle.
        int freeHead = _freeHead;
        for (int i = capacity - 1; i >= oldCapacity; i--)
        {
            _next[i] = freeHead;
            _prev[i] = FreeMarker;
            freeHead = i;
        }

        _freeHead = freeHead;
        _freeCount += capacity - oldCapacity;
    }

    /// <summary>Grows the backing arrays geometrically, used when an insertion runs out of free slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Grow()
    {
        int capacity = _next.Length;
        Resize(capacity == 0 ? DefaultCapacity : capacity * 2);
    }

    /// <summary>Takes a slot from the free list, stores <paramref name="value"/> in it and marks it detached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RentSlot(in T value)
    {
        if (_freeHead == NoLink)
        {
            Grow();
        }

        int slot = _freeHead;
        _freeHead = _next[slot];
        _freeCount--;

        _items[slot] = value;
        _next[slot] = NoLink;
        _prev[slot] = NoLink;
        return slot;
    }

    /// <summary>Pushes a slot back onto the free list and drops the value it held.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecycleSlot(int slot)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            // Without this the free list would keep every returned object alive for the lifetime of the list.
            _items[slot] = default!;
        }

        _prev[slot] = FreeMarker;
        _next[slot] = _freeHead;
        _freeHead = slot;
        _freeCount++;
    }

    /// <summary>Inserts a detached slot immediately before another slot of the ring.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertBefore(int anchor, int slot)
    {
        int previous = _prev[anchor];

        _next[slot] = anchor;
        _prev[slot] = previous;

        _prev[anchor] = slot;
        _next[previous] = slot;

        _count++;
        _version++;
    }

    /// <summary>Inserts a detached slot as the only node of an empty list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertIntoEmpty(int slot)
    {
        Debug.Assert(_count == 0 && _head == NoLink, "The list is not empty.");

        _next[slot] = slot;
        _prev[slot] = slot;

        _head = slot;
        _count = 1;
        _version++;
    }

    /// <summary>Inserts a detached slot as the first node, updating the head of the ring.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertFirst(int slot)
    {
        if (_count == 0)
        {
            InsertIntoEmpty(slot);
            return;
        }

        InsertBefore(_head, slot);
        _head = slot;
    }

    /// <summary>Inserts a detached slot as the last node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertLast(int slot)
    {
        if (_count == 0)
        {
            InsertIntoEmpty(slot);
            return;
        }

        // The list is circular, so the node before the head is the tail.
        InsertBefore(_head, slot);
    }

    /// <summary>Inserts a detached slot immediately before a live node, updating the head when needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertBeforeAnchor(int anchor, int slot)
    {
        InsertBefore(anchor, slot);

        if (_head == anchor)
        {
            _head = slot;
        }
    }

    /// <summary>
    /// Rebuilds the free list from scratch, recovering the slots left in limbo. Only valid on an empty list; the
    /// resulting chain is ordered by index, not by recency.
    /// </summary>
    private void RebuildFreeList()
    {
        Debug.Assert(_count == 0, "The free list can only be rebuilt when the list is empty.");

        int last = _next.Length - 1;
        bool clearItems = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        for (int i = 0; i < last; i++)
        {
            _next[i] = i + 1;
            _prev[i] = FreeMarker;

            if (clearItems)
            {
                _items[i] = default!;
            }
        }

        if (last >= 0)
        {
            _next[last] = NoLink;
            _prev[last] = FreeMarker;

            if (clearItems)
            {
                _items[last] = default!;
            }
        }

        _freeHead = last >= 0 ? 0 : NoLink;
        _freeCount = _next.Length;
    }

    /// <summary>Verifies that a slot belongs to the ring, in debug builds only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateLive(int slot)
    {
        // A live slot is the only one whose Prev is an index: free slots carry FreeMarker and detached ones NoLink.
        // The index bound also rejects handles that never came from this list and stale handles after a recycle.
        Debug.Assert(
            slot >= 0 && slot < _prev.Length && _prev[slot] >= 0,
            "The handle does not refer to a live node of this list.");
    }

    /// <summary>Gets a value indicating whether a slot is detached: in neither the ring nor the free list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDetachedSlot(int slot) =>
        slot >= 0 && slot < _prev.Length && _prev[slot] == NoLink;

    /// <summary>Returns the slot index of a handle that must refer to a detached node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DetachedSlot(PooledLinkedListNode node)
    {
        int slot = node.Index;
        Debug.Assert(IsDetachedSlot(slot), "The handle does not refer to a detached node of this list.");
        return slot;
    }

    /// <summary>Walks the ring without allocating; throws when the list is modified while enumerated.</summary>
    [DebuggerDisplay("Current = {Current}")]
    public struct Enumerator : IEnumerator<T>, IEnumerator
    {
        private readonly PooledLinkedList<T> _list;
        private readonly int _version;
        private int _slot;
        private int _remaining;
        private T? _current;

        internal Enumerator(PooledLinkedList<T> list)
        {
            _list = list;
            _version = list._version;
            _slot = list._head;
            _remaining = list._count;
            _current = default;
        }

        /// <summary>Gets the value of the node the enumerator is currently on.</summary>
        public T Current => _current!;

        object? IEnumerator.Current => _current;

        /// <summary>Advances the enumerator to the next node.</summary>
        /// <exception cref="InvalidOperationException">The list was modified after the enumerator was created.</exception>
        public bool MoveNext()
        {
            if (_version != _list._version)
            {
                throw new InvalidOperationException("Collection was modified after the enumerator was created.");
            }

            // The walk is bounded by the node count captured when the enumerator was created, because the ring has
            // no natural end: the Next of the last node points back at the head.
            if (_remaining <= 0)
            {
                _slot = NoLink;
                _current = default;
                return false;
            }

            _remaining--;
            _current = _list._items[_slot];
            _slot = _list._next[_slot];
            return true;
        }

        void IEnumerator.Reset()
        {
            if (_version != _list._version)
            {
                throw new InvalidOperationException("Collection was modified after the enumerator was created.");
            }

            _slot = _list._head;
            _remaining = _list._count;
            _current = default;
        }

        /// <summary>Does nothing: the enumerator owns no unmanaged resource.</summary>
        public void Dispose()
        {
        }
    }
}
