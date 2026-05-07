namespace ShadowCulling;

public sealed class AttachedProperty<T> : IAttachedProperty<T>
{
    private readonly ConditionalWeakTable<object, StrongBox<T>> storage = new();

    private T defaultValue;
    public ref T DefaultValue => ref defaultValue;

    private readonly ConditionalWeakTable<object, StrongBox<T>>.CreateValueCallback newBox;

    public AttachedProperty(T defaultValue = default!)
    {
        this.defaultValue = defaultValue;
        newBox = NewBox;
    }

    public static AttachedProperty<T> Create(T defaultValue = default!)
    {
        return new(defaultValue);
    }

    public T GetValue(object obj) => storage.GetValue(obj, newBox).Value!;

    public ref T GetValueRef(object obj) => ref storage.GetValue(obj, newBox).Value!;

    public ref T GetValueRef(object obj, out bool isNew)
    {
        isNew = false;
        if (!storage.TryGetValue(obj, out var box))
        {
            storage.Add(obj, box = newBox(obj));
            isNew = true;
        }
        return ref box.Value!;
    }

    public void SetValue(object obj, in T value)
    {
        storage.GetValue(obj, newBox).Value = value;
    }

    public bool Remove(object obj)
    {
        return storage.Remove(obj);
    }

    public void Clear()
    {
        storage.Clear();
    }

    public void ResetValues()
    {
        foreach (var box in storage)
        {
            box.Value.Value = DefaultValue;
        }
    }

    private StrongBox<T> NewBox(object obj) => new StrongBox<T>(DefaultValue);
}

public interface IAttachedProperty;

public interface IAttachedProperty<T> : IAttachedProperty
{
    ref T DefaultValue { get; }
    T GetValue(object obj);
    void SetValue(object obj, in T value);
    bool Remove(object obj);
    void Clear();
}