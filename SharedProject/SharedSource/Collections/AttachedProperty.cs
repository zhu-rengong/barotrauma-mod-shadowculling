using System.Diagnostics;

namespace ShadowCulling;

/// <summary>
/// A table of values attached to arbitrary objects, keyed by the objects themselves and held weakly. Reads never
/// insert an entry, and invalidating is a generation bump rather than a data write, so <see cref="InvalidateAll"/>
/// is O(1).
/// </summary>
public sealed class AttachedProperty<T>
{
    /// <summary>One stored value plus the generation it was written in.</summary>
    [DebuggerDisplay("Epoch = {Epoch}, Value = {Value}")]
    private sealed class Slot
    {
        /// <summary>The attached value.</summary>
        public T Value = default!;

        /// <summary>The generation this value was written in; <c>0</c> means never written.</summary>
        public long Epoch;
    }

    /// <summary>The live entries; an entry disappears together with its key.</summary>
    private readonly ConditionalWeakTable<object, Slot> slots = new();

    private readonly T defaultValue;

    /// <summary>The current generation; starts at <c>1</c>, so the <c>0</c> of a fresh slot is always stale.</summary>
    private long epoch = 1;

    /// <summary>Initializes a new table.</summary>
    public AttachedProperty(T defaultValue = default!)
    {
        this.defaultValue = defaultValue;
    }

    /// <summary>Creates a new table; equivalent to the constructor.</summary>
    public static AttachedProperty<T> Create(T defaultValue = default!) => new(defaultValue);

    /// <summary>Gets the value reported for keys without a live entry.</summary>
    public T DefaultValue => defaultValue;

    /// <summary>
    /// Gets the value attached to <paramref name="obj"/>, or <see cref="DefaultValue"/> when the key has no entry or
    /// its entry was invalidated. This is the hot read path: it never inserts an entry and never allocates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetValue(object obj)
    {
        if (slots.TryGetValue(obj, out Slot? slot)
            && Volatile.Read(ref slot.Epoch) == Volatile.Read(ref epoch))
        {
            return slot.Value;
        }

        return defaultValue;
    }

    /// <summary>
    /// Attaches <paramref name="value"/> to <paramref name="obj"/>, creating the entry when needed. Meant for the
    /// parallel workers of a pass: one writer per key.
    /// </summary>
    public void SetValue(object obj, in T value)
    {
        long current = Volatile.Read(ref epoch);

        Slot slot = GetOrCreateSlot(obj);
        slot.Value = value;
        // Published last, with release semantics: a reader that sees this generation also sees the value above.
        Volatile.Write(ref slot.Epoch, current);
    }

    /// <summary>
    /// Gets a reference to the value attached to <paramref name="key"/>, computing it through
    /// <paramref name="factory"/> when the entry is missing or was invalidated. The factory must be free of side
    /// effects (a lost race can run it twice), and the reference stays valid only while the key is alive and the
    /// entry is neither invalidated nor dropped.
    /// </summary>
    public ref T GetOrAdd<TKey>(TKey key, Func<TKey, T> factory) where TKey : class
    {
        long current = Volatile.Read(ref epoch);

        Slot slot = GetOrCreateSlot(key);
        if (Volatile.Read(ref slot.Epoch) != current)
        {
            slot.Value = factory(key);
            Volatile.Write(ref slot.Epoch, current);
        }

        return ref slot.Value;
    }

    /// <summary>
    /// Invalidates every value, so that all keys report <see cref="DefaultValue"/> again. O(1), allocation-free, but
    /// it must not run while another thread is writing; the call sites invalidate between passes.
    /// </summary>
    public void InvalidateAll() => Interlocked.Increment(ref epoch);

    /// <summary>
    /// Drops every entry, releasing the values instead of leaving them to be recomputed. Costs work proportional to
    /// the live entries, so it is meant for scenario changes rather than for every pass.
    /// </summary>
    public void Clear()
    {
        slots.Clear();
        Interlocked.Increment(ref epoch);
    }

    /// <summary>
    /// Gets the entry of <paramref name="key"/>, creating it when it does not exist yet. Inserts through
    /// <c>TryAdd</c> and falls back to the table's own factory, so it cannot throw.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Slot GetOrCreateSlot(object key)
    {
        if (slots.TryGetValue(key, out Slot? slot))
        {
            return slot;
        }

        Slot created = new();
        if (slots.TryAdd(key, created))
        {
            return created;
        }

        // Another thread inserted the same key in between. The callback is only invoked if that entry is already
        // gone again, which takes a concurrent Clear() - the callers keep that out of a running pass.
        return slots.GetValue(key, static _ => new Slot());
    }
}
