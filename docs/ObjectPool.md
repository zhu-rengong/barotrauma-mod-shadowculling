# ObjectPool 设计说明

> 相关文件
> - 池实现：`SharedProject/SharedSource/Collections/ObjectPool.cs`（`IPool<T>` / `PoolStatistics` / `ObjectPool<T>`）
> - 租赁句柄：`SharedProject/SharedSource/Collections/PooledLease.cs`
> - 被池化的对象：[`PooledLinkedList.md`](./PooledLinkedList.md)（`PooledLinkedList<T>` / `PooledLinkedListNode`）
> - 唯一调用点：`ClientProject/ClientSource/Culling/PluginClient.cs`（`segmentListPool`，在 `Cull<T>` 中使用）
>
> 环境：.NET 8 / `Nullable=enable` / `LangVersion=latest`，无新增 NuGet 依赖（`Microsoft.Extensions.ObjectPool`、`ArrayPool<T>` 仅作设计参考）。

---

## 目录

1. [背景：旧实现的问题](#1-背景旧实现的问题)
2. [重构目标](#2-重构目标)
3. [总体架构](#3-总体架构)
4. [API 一览](#4-api-一览)
5. [数据结构](#5-数据结构)
6. [核心流程](#6-核心流程)
7. [并发正确性论证](#7-并发正确性论证)
8. [与调用点的集成](#8-与调用点的集成)
9. [性能特性](#9-性能特性)
10. [基础概念小抄](#10-基础概念小抄)
11. [契约与注意事项](#11-契约与注意事项)
12. [观测与调优](#12-观测与调优)
13. [术语速查表](#13-术语速查表)
14. [附录 A：小白版 —— 食堂碗架比喻](#14-附录-a小白版--食堂碗架比喻)

> 只想快速理解设计意图的读者，可以直接跳到最后的[附录 A](#14-附录-a小白版--食堂碗架比喻)；
> 需要精确语义、参数与并发论证的读者，从第 1 节顺序阅读。

---

## 1. 背景：旧实现的问题

旧实现（67 行）只有"一个 `ConcurrentQueue<T>` + 工厂委托"：

```csharp
private readonly ConcurrentQueue<T> _objectPool;
public T Get() { ... TryDequeue ... ?? _objectFactory(); }
public void Return(T item)
{
    if (_objectPool.Count < _maxCapacity)   // ← 问题所在
        _objectPool.Enqueue(item);
}
public int Count => _objectPool.Count;      // ← 也可能是 O(n)
```

| # | 问题 | 说明 |
| --- | --- | --- |
| 1 | **容量检查有竞态** | `Count < max` 与 `Enqueue` 之间存在窗口期，两个线程可能同时通过检查，导致超出 `maxCapacity`（TOCTOU 问题） |
| 2 | **`ConcurrentQueue.Count` 开销大** | 需要在并行热路径上遍历/汇总分段，且在 `Return` 这种高频调用上是纯浪费 |
| 3 | **单点 CAS 争用** | 所有线程争抢同一个队列的头/尾指针，核心越多越容易"缓存行乒乓" |
| 4 | **没有重置机制** | 归还对象后状态由调用方手工清理（如清空列表），一旦遗漏就是脏数据串扰 |
| 5 | **没有预热 / 溢出策略 / 可观测性** | 超容量对象被静默丢弃且不可观测；没有创建次数、命中率等指标 |
| 6 | **没有作用域式 API** | 只能手写 `Get()` / `Return()`，异常或提前 `return` 时会漏归还 |

---

## 2. 重构目标

| 维度 | 结论 |
| --- | --- |
| 总体 | 全面重构，按现代池化设计（参考 `Microsoft.Extensions.ObjectPool` / `ArrayPool<T>`）重写 |
| API 兼容性 | **允许破坏性变更**（`Get` → `Rent`），并同步修改调用点 |
| 线程模型 | **线程本地缓存 + 全局兜底** |
| 对象重置 | **委托钩子**（`onRent` / `onReturn` / `onDiscard`），不污染对象类型 |
| 附加能力 | 预热、容量配置、溢出策略、统计快照、`IDisposable` 租赁、生命周期管理 |

---

## 3. 总体架构

两级结构：**线程本地 LIFO 缓存**（L1）+ **全局无锁队列**（L2）。

```
                    ┌──────────────────── Rent() ─────────────────────┐
                    │                                                 │
             L1 线程本地缓存 (LIFO 数组栈)                             │
             ┌───────────────────────────┐                           │
             │  命中? ──是──► 弹出 ───────┼──────────────────────────►│ 返回对象
             └───────────┬───────────────┘                           │  (调用 onRent)
                         │ 否                                        │
                         ▼                                           │
             L2 全局池 ConcurrentQueue<T>                            │
             ┌───────────────────────────┐                           │
             │ TryDequeue 成功? ──是──► ──┼──────────────────────────►│
             └───────────┬───────────────┘                           │
                         │ 否                                        │
                         ▼                                           │
                    工厂创建 Create() ─────────────────────────────►─┘

                    ┌─────────────────── Return(item) ───────────────┐
                    │  onReturn(item)  ← 状态重置                     │
                    │        │                                        │
                    │        ▼                                        │
                    │  L1 本地缓存未满? ──是──► 压栈，结束             │
                    │        │ 否                                     │
                    │        ▼                                        │
                    │  原子占额度: ++sharedCount <= maxCapacity ?      │
                    │        ├── 是 ──► 入全局队列，结束               │
                    │        └── 否 ──► 回滚额度 ──► Discard(丢弃)     │
                    └─────────────────────────────────────────────────┘

        线程终止 ──► ~ThreadLocalCache() ──► 残留对象回填全局池 / 超容量则丢弃
```

设计要点：

- **L1 只被所属线程访问**，因此完全不需要同步（连 `Interlocked` 都不用）。
- **L2 承担跨线程搬运**：本地不够时下沉、本地满了时上溢。
- 任何对象都**不会被无条件保留**：放不下就丢弃（并触发 `onDiscard`），池永远不会无限增长。

---

## 4. API 一览

### 4.1 接口

```csharp
public interface IPool<T> where T : class
{
    T Rent();
    void Return(T item);
}
```

抽出接口的意义：调用方可以面向契约编程，`PooledLease<T>` 也依赖 `IPool<T>` 而非具体实现。

### 4.2 `ObjectPool<T>` 成员

| 成员 | 签名 | 说明 |
| --- | --- | --- |
| 构造 | `ObjectPool(Func<T> factory, Action<T>? onRent = null, Action<T>? onReturn = null, Action<T>? onDiscard = null, int maxCapacity = 1024, int maxPerThreadCapacity = 8, int initialCapacity = 0, bool drainOnThreadExit = true)` | 全部可选命名参数 |
| 取用 | `T Rent()` | 本地 → 全局 → 工厂 |
| 归还 | `void Return(T item)` | `onReturn` 重置 → 本地 → 全局 → 丢弃 |
| 租赁 | `PooledLease<T> RentLease(out T item)` | 可 `using`，作用域结束自动归还 |
| 预热 | `void Prewarm(int count)` | 预创建对象进全局池 |
| 清空 | `void Clear()` | 丢弃当前线程缓存 + 全局池内所有对象，池仍可用 |
| 释放 | `void Dispose()` | 清空 + 置释放标记；之后 `Rent` 抛异常，`Return` 静默丢弃 |
| 计数 | `int Count` | **仅全局池**对象数（O(1)），不含线程缓存 |
| 容量 | `int Capacity`、`int MaxPerThreadCapacity` | 两级容量上限 |
| 状态 | `bool IsDisposed` | — |
| 统计 | `PoolStatistics Statistics` | 只读快照，含 `HitRate` 与 `ToString()` 日志串 |

### 4.3 用法示例

```csharp
private static readonly ObjectPool<List<int>> s_pool = new(
    () => new List<int>(),
    onReturn: static list => list.Clear());

// 推荐：作用域式，异常/提前返回也不会漏归还
using var lease = s_pool.RentLease(out List<int> buffer);
buffer.Add(42);

// 也可显式租还
List<int> manual = s_pool.Rent();
s_pool.Return(manual);
```

### 4.4 `PooledLease<T>`

```csharp
public struct PooledLease<T> : IDisposable where T : class
{
    public T Value { get; }      // 已归还时抛 InvalidOperationException
    public bool IsValid { get; } // 是否仍持有对象
    public void Dispose();       // 幂等：先清空自身状态，再归还
}
```

- `Dispose()` 先把 `_pool` / `_item` 置空再调用 `pool.Return(item)`——即使归还钩子抛异常也不会二次归还。
- **可变结构体：不要复制**。每个副本都持有同一个对象，会各自归还一次（同一个变量重复 `Dispose` 才是安全的）。

### 4.5 `PoolStatistics`

| 字段 | 含义 |
| --- | --- |
| `SharedCount` / `MaxCapacity` | 全局池当前数量 / 上限 |
| `LocalCount` / `MaxPerThreadCapacity` | **读取快照的那个线程**的缓存数量 / 上限 |
| `CreatedCount` | 工厂创建次数（含 `Prewarm` 预先创建的对象） |
| `RentedCount` | `Rent()` 成功次数 |
| `ReusedCount` | 由池提供的租出次数（本地缓存或全局池命中） |
| `ReturnedCount` | `Return()` 成功交回池中的次数（不含 `Prewarm` 与线程退出时的内部转移） |
| `DiscardedCount` | 被丢弃次数（放不下 / 已释放） |
| `HitRate` | `ReusedCount / RentedCount`，复用命中率 |

> 计数关系：只要没有调用过 `Prewarm`，就有 `ReusedCount + CreatedCount == RentedCount`。
> 预热会"只创建、不租出"，因此 `Rented - Created` 可能为负，**不能用它反推复用次数**——请直接读 `ReusedCount`。
> `ReusedCount` 只在真正发生复用时累加（进入缓存或全局池的对象在被租出时才计入）。

---

## 5. 数据结构

| 字段 | 类型 | 作用 |
| --- | --- | --- |
| `_factory` | `Func<T>` | 池空时的兜底创建 |
| `_onRent` / `_onReturn` / `_onDiscard` | `Action<T>?` | 三个生命周期钩子 |
| `_sharedPool` | `ConcurrentQueue<T>` | L2 全局池，无锁 |
| `_maxCapacity` | `int` | 全局池容量上限 |
| `_maxPerThreadCapacity` | `int` | 单线程缓存上限；`0` 表示禁用线程本地缓存 |
| `_slot` | `int` | 本池在"线程静态缓存数组"中的下标；`-1` 表示退化为纯全局池 |
| `_disposed` | `int` | 释放标记（原子访问） |
| `_sharedCount` | `int` | 全局池对象数**上界**（原子访问，用于容量判定与 `Count`） |
| `_createdCount` / `_rentedCount` / `_reusedCount` / `_returnedCount` / `_discardedCount` | `long` | 只读统计，全部 `Interlocked` |
| `t_caches` | `[ThreadStatic] ThreadLocalCache?[]?` | **每个线程一份**的缓存数组，按 `_slot` 索引 |
| `s_slotCounter` | `static int` | 槽位分配器（按泛型类型 `T` 各自一份） |

### 5.1 为什么用 `[ThreadStatic]` 而不是 `ThreadLocal<T>`

1. **快**：`[ThreadStatic]` 编译成"线程本地存储（TLS）固定偏移"的直接访问；`ThreadLocal<T>.Value` 要经过属性调用和内部表查找。
2. **可回收**：线程结束后 `t_caches` 数组不可达 → 其中的缓存对象成为垃圾 → **终结器可以把它内部残留的对象交还全局池**。而 `ThreadLocal<T>`（默认 `trackAllValues: false`）不保证能枚举/回收各线程的值。
3. **注意坑**：`[ThreadStatic]` 字段的**初始化器只对第一个访问它的线程生效**，所以必须"判空 + 懒创建"。

### 5.2 为什么还要 `_slot`

`[ThreadStatic]` 是**泛型类**的静态字段：`ObjectPool<A>` 与 `ObjectPool<B>` 各有一份 `t_caches`；但同一个 `ObjectPool<A>` 若创建了多个实例，它们会共用同一个线程静态字段。因此每个池实例分配一个下标：

```csharp
int slot = Interlocked.Increment(ref s_slotCounter) - 1;
_slot = slot < MaxPoolSlots ? slot : -1;   // 超过 16 个池实例则退化为纯全局池
t_caches[_slot]  // ← 这才是"这个池 × 这个线程"的专属缓存
```

这样"每线程每池一份缓存"就成立了，且热路径只有一次数组索引。

---

## 6. 核心流程

### 6.1 `Rent()`

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public T Rent()
{
    if (IsDisposed) { ThrowDisposed(); }

    T? item = PeekOrCreateCache()?.TryPop();               // L1

    if (item == null && _sharedPool.TryDequeue(out T? sharedItem))   // L2
    {
        Interlocked.Decrement(ref _sharedCount);
        item = sharedItem;
    }

    if (item == null) { item = Create(); }                 // 工厂
    else { Interlocked.Increment(ref _reusedCount); }

    Interlocked.Increment(ref _rentedCount);
    _onRent?.Invoke(item);
    return item;
}
```

- `PeekOrCreateCache()`：`_slot < 0` 时返回 `null`（纯全局模式）；否则**懒创建**本线程的缓存。
- 只有 L1 空了才触碰 L2（`ConcurrentQueue.TryDequeue` 是一次 CAS，能省则省）。
- `ThrowDisposed()` 标了 `[MethodImpl(NoInlining)]`，把异常构造代码移出热路径。

### 6.2 `Return(item)`

```csharp
public void Return(T item)
{
    if (item is null) { return; }
    if (IsDisposed) { Discard(item); return; }

    _onReturn?.Invoke(item);                               // ① 先重置

    ThreadLocalCache? cache = PeekOrCreateCache();          // ② 再进 L1
    if (cache != null && cache.TryPush(item))
    {
        Interlocked.Increment(ref _returnedCount);
        return;
    }

    if (!TryStoreShared(item))
    {
        Discard(item);                                      // ③ 溢出到 L2 失败 → 丢弃
        return;
    }

    Interlocked.Increment(ref _returnedCount);              // 计数在这里，不在 TryStoreShared 内
}
```

顺序至关重要：**先执行 `onReturn` 重置状态，再入库**。这是"下一个租用者拿到的对象一定是干净的"的唯一保证点。

### 6.3 容量管理：原子"占额度 + 回滚"

```csharp
private bool TryStoreShared(T item)
{
    if (Interlocked.Increment(ref _sharedCount) <= _maxCapacity)
    {
        _sharedPool.Enqueue(item);
        return true;                            // _returnedCount 由 Return() 计入（见 §7）
    }

    Interlocked.Decrement(ref _sharedCount);   // 超额，回滚
    return false;
}
```

- **先占额度、失败回滚**：额度是原子变化的，因此**永远不会超过 `maxCapacity`**，彻底消除旧实现的 TOCTOU 竞态。
- 代价：在"已占额、未入队"的极短窗口内，`_sharedCount` 比队列实际长度大 —— 所以它是队列长度的**上界**。表现是"偶尔保守地少收一个对象"，绝不会溢出。
- 由此 `Count` 退化为一次 `Volatile.Read`，O(1)。

### 6.4 `Prewarm(count)`

```csharp
for (int i = 0; i < count; i++)
{
    T item = Create();
    _onReturn?.Invoke(item);            // 预热对象同样走"入库前重置"
    if (!TryStoreShared(item)) { Discard(item); break; }   // 额度被并发占满则停止
}
```

预热对象统一进**全局池**（而非当前线程缓存），这样第一批真正使用它的 worker 线程就能直接命中。

### 6.5 `Clear()` / `Dispose()`

```csharp
public void Clear()
{
    DrainCache(PeekCache());   // 只能清空"当前线程"的缓存
    DrainSharedPool();
}

public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }   // 幂等

    ThreadLocalCache? cache = PeekCache();
    if (cache != null && _slot >= 0) { t_caches![_slot] = null; }  // 解绑当前线程缓存

    DrainCache(cache);
    DrainSharedPool();
}
```

- 其他线程的缓存无法安全触碰（会引入竞态），只能等各自线程结束时的终结器处理。
- 释放后：`Rent()` 抛 `ObjectDisposedException`；`Return()`（含租赁 `Dispose`）**静默丢弃** —— 这让并行关闭场景下"晚到的归还"不会炸掉程序。

### 6.6 线程本地缓存（`ThreadLocalCache`）

```csharp
internal T? TryPop()
{
    int index = _count - 1;
    if (index < 0) { return null; }
    T? item = _items[index];
    _items[index] = null;    // 关键：置 null，否则该槽位会长期强引用已归还对象
    _count = index;
    return item;
}

internal bool TryPush(T item)
{
    int index = _count;
    if (index >= _items.Length) { return false; }   // 满了 → 交给调用者溢出到全局池
    _items[index] = item;
    _count = index + 1;
    return true;
}
```

- 定长数组 + `_count` 当栈顶指针，**LIFO**（后进先出）。
- **LIFO 的意义**：刚被本线程使用过的对象，其内存（以及它引用的子结构）大概率还在 CPU 的 L1/L2 缓存里，取回来即是缓存命中；FIFO 拿到的是很久以前的对象，可能已从缓存中淘汰，需要从主存重新加载。
- 无同步：该对象只被所属线程访问。

### 6.7 线程退出回填

```csharp
~ThreadLocalCache()
{
    if (!_drainOnThreadExit) { return; }
    try { _owner.DrainThreadLocalCache(this); }
    catch { /* 绝不能让终结器线程抛异常 */ }
}

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
```

- 线程结束后 `t_caches` 不可达 → 缓存对象进入终结队列 → 残留对象回填全局池，而不是随线程一起被回收。
- **不在终结器线程调用用户回调**（这里只更新队列与计数）：终结器线程全进程只有一个，在其中执行任意用户代码可能死锁或严重拖慢所有对象回收。
- `drainOnThreadExit: false` 时构造里直接 `GC.SuppressFinalize(this)`，免掉终结开销。
- 实践提示：`Parallel.For` 用的是**线程池线程，不会退出**，因此这条路径主要是为临时线程兜底。

---

## 7. 并发正确性论证

**容量不变式**

```
_sharedCount 的实际入队数 ≤ 队列长度 + 正在入队的线程数
_sharedCount ≤ _maxCapacity   恒成立
```

每次占额都做原子自增并立即判定，超额立刻回滚，因此不存在两个线程"同时通过"的可能。

**可见性与发布**

- 参与正确性判断的 `_disposed` / `_sharedCount` 一律通过 `Volatile.Read` / `Interlocked` 访问，避免"缓存到寄存器"和指令重排导致读到过期值。
- 对象的**发布**通过 `ConcurrentQueue`（内部有内存屏障）或线程本地数组完成，不存在"构造未完成就被别人看到"的问题。

**统计口径**

- 统计计数器只用于观测，**不参与任何逻辑判定**，因此允许"不是同一瞬间的精确快照"，换取热路径上的零阻塞。
- `Reused` 由两条命中路径（本地缓存、全局池）分别累加，**不依赖 `Rented - Created` 推导**——预热会创建但不租出，差值法在 `Prewarm` 之后不成立。
- 每个对象在"交回池中"时最多被计入 `Returned` 一次：计数点放在 `Return()` 的两条入库分支上，而不是放在共用的入库函数里，这样终结器把线程缓存里的对象搬进全局池时不会重复计数。

**异常/失败路径**

- 工厂返回 `null` → 抛 `InvalidOperationException`（明确的编程错误，而不是静默产生坏数据）。
- 池满 → 丢弃 + `onDiscard`，不抛异常（避免"归还失败"反过来把调用方的正常流程炸掉）。
- 钩子抛异常 → `Return` 中对象不再入库（等价于丢弃）；终结器路径全程 `try/catch`。

---

## 8. 与调用点的集成

### 8.1 池定义

```csharp
// Object pooling for performance
// Each worker thread keeps a small cache of clipping lists, and every list is emptied (nodes go back to its
// internal free list, so their memory is kept) before it is stored again.
private static ObjectPool<PooledLinkedList<Segment>> segmentListPool = new(
    static () => new PooledLinkedList<Segment>(),
    onReturn: static list => list.Clear());
```

- `onReturn` 是"归还前清空"的兜底：`PooledLinkedList.Clear()` 会把槽位全部回收进列表内部的空闲链并**保留底层数组容量**，因此节点内存继续复用，不会因为归还而释放。
- `static` 委托不捕获任何状态，避免隐式闭包分配。

### 8.2 租用与自动归还

```csharp
private static void Cull<T>(List<T> entities, int fromInclusive, int toExclusive, ref int totalCulled) where T : Entity
{
    Span<Segment> entityEdges = stackalloc Segment[8];
    Span<Segment> edgeClipBuffer = stackalloc Segment[3];
    // The lease returns the list to the pool when this method exits, including on early returns and exceptions.
    using var clippingEdgesLease = segmentListPool.RentLease(out PooledLinkedList<Segment> clippingEdges);
    int entitiesCulled = 0;
    ...
    Interlocked.Add(ref totalCulled, entitiesCulled);
}
```

- 由 `using` 租赁自动归还，**异常或提前退出也不会泄漏列表**（旧代码路径异常时列表会永久丢失）。
- 方法内的 `CULL:` / `SKIP:` 跳转与循环内的 `clippingEdges.Clear()`（每个实体的中间清理）全部保持不变，只是把"归还时机"从手工调用改成了作用域结束。
- 列表本身也已重设计为「结构体数组 + 索引句柄」（见 [`PooledLinkedList.md`](./PooledLinkedList.md)），本节的调用形式同步更新为句柄式 API。

### 8.3 观测接入

性能日志（仅在既有的 `DebugLoggingEnabled` 门控下输出）追加一行：

```csharp
$"ClipPool: {segmentListPool.Statistics}"
// 例：ClipPool: Rent=1024, Create=6, Reuse=1018, Return=1024, Discard=0,
//     Hit=99.4%, Shared=0/1024, Local=1/8
```

---

## 9. 性能特性

| 操作 | 典型路径 | 同步代价 | 分配 |
| --- | --- | --- | --- |
| `Rent()`（L1 命中） | 数组取值 + 递减 `_count` + 2 次计数器自增 | 无锁、无 CAS（仅统计用的原子自增） | 无 |
| `Rent()`（L2 命中） | `TryDequeue`（CAS） | 一次 CAS | 无 |
| `Rent()`（工厂） | `factory()` | 无 | 有（不可避免） |
| `Return()`（L1 接收） | `onReturn` + 数组写入 | 无锁 | 无 |
| `Return()`（上溢 L2） | `ConcurrentQueue.Enqueue`（CAS） | 一次 CAS | 无 |
| `Return()`（丢弃） | `onDiscard` | 无锁 | 无 |
| `Count` | `Volatile.Read` | 无 | 无 |
| `Statistics` | 数次原子读 + 一次 TLS 查表 | 无 | 结构体快照 |

优化手段小结：

- `[MethodImpl(AggressiveInlining)]` 标注所有热路径小方法。
- 只在必要时触碰全局队列（L1 空/满）。
- 异常构造路径 `NoInlining` 移出热路径。
- 定长数组代替 `List<T>`，避免扩容与额外间接层。

---

## 10. 基础概念小抄

### 10.1 多线程的三个麻烦

| 概念 | 解释 |
| --- | --- |
| **竞态条件**（race condition） | 结果取决于线程调度顺序，多由"检查—然后—动作"这种非原子组合引起（旧代码的 `if (Count < max) Enqueue`） |
| **可见性**（visibility） | 一个线程的写操作不保证立刻被其他核心看到（CPU 缓存、寄存器缓存） |
| **指令重排** | CPU/编译器在保证单线程语义的前提下调整顺序，多线程下可能观察到"不该出现的中间状态" |

### 10.2 锁（`lock`）

同一时刻只允许一个线程进入临界区，其他线程**阻塞等待**。

- 优点：一次解决竞态 + 可见性（进出锁隐含内存屏障）。
- 代价：上下文切换（微秒级）、强制串行、极端情况死锁。高频短操作不值得。

### 10.3 原子操作与 `Interlocked`

**原子** = 硬件保证"要么全做完、要么完全没发生"，中间态不可被观察。

```csharp
Interlocked.Increment(ref _sharedCount);
Interlocked.Decrement(ref _sharedCount);
Interlocked.Exchange(ref _disposed, 1);
Interlocked.Read(ref _longField);
```

底层是带 `LOCK` 前缀的 CPU 指令，不需要锁，也不会阻塞线程。

### 10.4 CAS（Compare-And-Swap）

```csharp
Interlocked.CompareExchange(ref location, newValue, expected);
// 若 location 当前 == expected 则写入 newValue；无论如何都返回 location 的旧值
```

"乐观并发"的思想：我猜变量还是我读到的值 → 我就写；否则重读重算重试。

```csharp
// 手写"无锁累加"的经典形式
int old;
do { old = _value; }
while (Interlocked.CompareExchange(ref _value, old + 1, old) != old);
```

两个坑：
- **自旋浪费 / 缓存行乒乓**：多个核心争抢同一地址时反复失败重试，可能比锁更慢。
- **ABA 问题**：值从 A → B → A，CAS 误以为没被动过（`ConcurrentQueue` 内部用标记/版本等手段规避）。

### 10.5 "无锁"（lock-free）

> 线程之间**不需要互相阻塞等待**：即使某个线程被 OS 暂停，其他线程依然能继续推进整个数据结构。

收益：无阻塞、无上下文切换、无死锁。代价：所有并发正确性都要靠原子操作和不变式自己证明，代码更难写更难读。

### 10.6 `Volatile`

```csharp
public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
```

"每次都去内存读，别缓存到寄存器，也别和相邻指令换顺序"。写操作对应 `Volatile.Write`；`Interlocked` 系列本身带屏障语义，计数器无需再加 `Volatile`。

### 10.7 `[ThreadStatic]` / `ThreadLocal<T>` / 普通静态字段

| 方式 | 语义 | 速度 | 备注 |
| --- | --- | --- | --- |
| 普通静态字段 | 全进程一份，所有线程共享 | 最快 | 需要同步或原子操作保护 |
| `[ThreadStatic]` | **每个线程一份副本** | 极快（TLS 固定偏移） | 字段初始化器只对第一个线程生效，必须懒创建 |
| `ThreadLocal<T>` | 包装类，`.Value` 按线程取值 | 较慢（属性 + 表查找） | 支持枚举所有线程的值、可 `Dispose` |

### 10.8 LIFO 与缓存局部性

栈（后进先出）优先取回"刚被本线程用过"的对象，其内存大概率仍在 CPU L1/L2 缓存中（命中为纳秒级）；队列（先进先出）可能取到很久未用、已被换出到主存的冷数据（差约两个数量级）。

### 10.9 终结器（finalizer）

`~Type()` 形式的析构方法。GC 发现对象不可达且定义了终结器时，不会立即回收，而是排入终结队列，由**全进程唯一的终结器线程**调用一次。

- 适用于"对象死亡时需要归还外部资源"的场景（这里就是"归还对象到全局池"）。
- 终结器线程是稀缺资源：其中不得执行长耗时/可能阻塞或抛异常的用户代码。

---

## 11. 契约与注意事项

1. **不检测重复归还 / 跨池归还**：检测需要给每个对象附加状态（如 `ConditionalWeakTable`），成本高于池化收益；因此仅在文档中声明契约。同一对象必须归还给"交给它的那个池"，且只归还一次，归还后不得继续使用。
2. **钩子必须轻量、幂等、不抛异常**：`onRent` / `onReturn` 在租借线程上同步执行，直接落在热路径中；`onDiscard` 在丢弃路径执行，且在**终结器驱动的回填路径中不会被调用**。
3. **`PooledLease<T>` 是可变结构体，不要复制或长期保存**：每个副本都会各自归还一次。同一变量重复 `Dispose` 是安全的（幂等）。
4. **`Count` 语义收窄**：只统计全局池，不含线程本地缓存；要看全貌请用 `Statistics`。
5. **`_sharedCount` 是上界，不是精确值**：不要用它做精确断言。
6. **`maxPerThreadCapacity: 0`** → 退化为纯全局池（等价旧行为）；**`maxCapacity: 0`** → 全局不保留对象，只在各线程缓存内循环。
7. **可空性**：`T` 必须为引用类型（`where T : class`）；工厂返回 `null` 会立刻抛异常，避免坏数据流入池中。
8. **`Dispose()` 之后**：`Rent()` 抛 `ObjectDisposedException`；`Return()` 静默丢弃。因为静态池通常与进程同生命周期，实际使用中一般不会调用 `Dispose()`。

---

## 12. 观测与调优

### 12.1 日志字段怎么读

```
ClipPool: Rent=1024, Create=6, Reuse=1018, Return=1024, Discard=0, Hit=99.4%, Shared=0/1024, Local=1/8
```

| 字段 | 含义 | 期望 |
| --- | --- | --- |
| `Rent` / `Create` | 租借次数 / 实际分配次数 | `Create` 在启动后应长期不增长 |
| `Reuse` / `Hit` | 复用次数 / 命中率 | 越高越好，稳定在 99% 以上说明池生效 |
| `Return` | 归还次数 | 与 `Rent` 接近；长期明显偏低说明有泄漏 |
| `Discard` | 丢弃次数 | 偶发正常；持续增长说明容量配置偏小 |
| `Shared` / `Local` | 全局池 / 当前线程缓存占用 | 长期贴着上限说明容量需要上调 |

### 12.2 参数调优

| 参数 | 含义 | 调优建议 |
| --- | --- | --- |
| `maxCapacity` | 全局池上限（默认 1024） | 按"并发峰值 × 每批对象数 × 安全系数"估算 |
| `maxPerThreadCapacity` | 单线程缓存上限（默认 8） | 常等于最大并发度；太大会导致对象分散在各线程无法流动 |
| `initialCapacity` | 预热数量 | 设为预期峰值并发数，消除启动期分配抖动 |
| `drainOnThreadExit` | 线程退出是否回填全局池 | 临时线程多时保持 `true`；纯固定线程池场景可设 `false` 免掉终结开销 |

---

## 13. 术语速查表

| 术语 | 一句话解释 |
| --- | --- |
| 对象池 | 复用对象以减少分配与 GC 压力的容器 |
| 竞态条件 | 结果依赖线程调度顺序，通常源自非原子的"检查—动作"组合 |
| TOCTOU | Time-Of-Check to Time-Of-Use，检查与使用之间的窗口期 |
| 原子操作 | 硬件保证不可分割、中间态不可被观察的操作 |
| `Interlocked` | .NET 暴露原子操作的类（自增、加减、交换、CAS、原子读） |
| CAS | 比较并交换，"确认仍是旧值才写入"，失败则重试 |
| 无锁（lock-free） | 不用阻塞锁，线程不会互相等待或死锁 |
| 缓存行乒乓 | 多核心争抢同一内存地址导致的缓存同步开销 |
| ABA 问题 | 值 A→B→A 使 CAS 误判为"未被修改" |
| `Volatile` | 禁止寄存器缓存与重排，保证跨线程可见性 |
| 内存屏障 | 对读写顺序与可见性的编译/硬件约束 |
| `[ThreadStatic]` | 每线程一份副本的静态字段，访问极快 |
| `ThreadLocal<T>` | 按线程取值的包装类，更灵活但更慢 |
| LIFO / FIFO | 后进先出（栈）/ 先进先出（队列）；前者缓存局部性更好 |
| 终结器 `~T()` | 对象回收前的回调，运行在唯一的终结器线程上 |
| `ConcurrentQueue<T>` | 无锁并发队列，内部基于 CAS 与分段实现 |
| 不变式（invariant） | 任何时刻都必须成立的条件，是并发正确性的论证依据 |

---

## 14. 附录 A：小白版 —— 食堂碗架比喻

这一节不出现任何术语，用"食堂窗口和碗架"把整个设计重讲一遍。读完它再回头看前面的章节，会顺很多。

### 14.1 为什么需要对象池

程序每次 `new` 一个对象，就像**客人来了就去买一个新碗**，用完直接扔。买碗（分配内存）和扔碗（垃圾回收）都费时间，客人一多，厨房就忙不过来，游戏就会掉帧。

对象池就是：**用完的碗不扔，放回碗架**，下一位客人直接拿；只有碗架上没碗了，才去买新的。

在我们的 Mod 里，"碗"就是 `Cull` 每批次要用的那个线段切割列表。

### 14.2 麻烦：多个窗口同时伸手

CPU 有 4 个核心，相当于食堂开了 4 个窗口同时打饭。大家一起伸手去同一个碗架上拿碗，就会出问题：

- **撞手**：两个窗口都看到"还剩 1 个碗"，都伸手 → 拿了 2 个，数量乱了。这叫**竞态条件**。
- **看不见**：A 窗口拿了碗走，B 窗口还以为碗架上满满当当。这叫**可见性问题**（每个核心都有自己的小本本/缓存，不会立刻同步）。

旧代码里就有"撞手"的写法：

```csharp
if (碗架上还有空位)      // 两个人同时看到"还有 1 个空位"
    把碗放回去;          // 两个人一起放 → 超员
```

### 14.3 最直白的解法：上锁

给碗架装把锁：谁要动碗架，先锁门，外面的人排队等。

- 好处：简单、绝不会出错。
- 坏处：**排队浪费时间**；更糟的是，万一有人拿着钥匙在里面睡着了，所有人干等（死锁）。

几纳秒就能做完的小事，不值得排队。

### 14.4 更好的办法：原子操作

不去锁碗架，改成先动"计数器"。

**原子操作** = 这个动作小到半路不可能被插队，硬件保证"要么全做完、要么啥都没发生"。像**旋转门上的计数器**：四个人同时过，也不会数错。

我们的做法是"先占位，超了就退回"：

```
要放一个碗 → 计数器 +1，变成 5
   ├─ 5 没超过上限(4) → 真的把碗放进去
   └─ 5 超过上限(4)   → 计数器 -1 退回，碗不放了
```

这样**永远不可能超员**，也不需要任何锁。

### 14.5 CAS：不排队也能安全修改

想象一张便签，我读的时候写着 3。我要改成 4，但怕别人偷偷改过，于是说：

> "如果现在还是 3，就改成 4；否则算了，我重新看一眼再来。"

这个"先确认没被改过才动手"的动作就叫 **CAS（比较并交换）**。

- 被别人抢先改了 → 这次不算，重看再试（叫重试/自旋）。
- 全程没有人需要排队等别人。

`ConcurrentQueue`（下面说的"大仓库"）内部就是这么做的。

### 14.6 "无锁"是什么意思

不是"没有竞争"，而是：

> **谁都不用站在门口等别人。** 就算某个线程被操作系统临时冻住，其他线程照样能干自己的活。

好处是不排队、不死锁；坏处是这种代码很难写，所有正确性都要自己证明。

### 14.7 核心设计：每个窗口配一个小碗柜

这就是"线程本地缓存"（`ThreadLocalCache`）：

```
每个窗口(线程)有一个自己的小碗柜
   ├─ 要碗 → 先摸自己的柜子（不用跟任何人抢，超快）
   │        └─ 柜子空了 → 去大仓库(ConcurrentQueue)拿
   │                    └─ 仓库也空了 → 买个新的（工厂创建）
   └─ 还碗 → 先洗干净，放回自己的柜子
            └─ 柜子满了 → 放进大仓库
                        └─ 仓库也满了 → 扔掉
```

因为**自己的柜子只有自己碰**，所以连计数器都不需要，快得离谱。

"两级缓存"就是：**每个窗口的小柜子（L1）** + **共用的大仓库（L2）**。

### 14.8 `[ThreadStatic]`：每个线程自己的白板

普通 `static` 变量好比店里**一块公用白板**，四个人一起写肯定乱。
`[ThreadStatic]` 则是**每个窗口旁边各一块自己的白板**，你写你的我写我的，互不干扰——所以不需要锁。

那代码里的 `_slot`（槽位编号）是干嘛的？白板是"按类型"分发的（`ObjectPool<碗>` 一块、`ObjectPool<盘子>` 一块）。如果同一个类型你建了 3 个碗架，它们会共用同一块白板，于是给每个碗架编号：第 1 格放 1 号碗架的柜子，第 2 格放 2 号的……这样就不会串台。

### 14.9 为什么先拿"刚放回去的那个碗"

柜子的取用顺序是**后进先出**（LIFO，就是个栈）。

原因很朴素：**刚用过的碗还是热乎的**。CPU 读内存有快慢之分，刚用过的数据大概率还留在 CPU 的小缓存里（快约 100 倍）；换成"先进先出"，你拿到的可能是很久没动、早已凉透的碗，得重新去仓库深处搬。

### 14.10 放回去之前先洗干净（`onReturn`）

碗放回柜子前必须洗，也就是代码里的：

```csharp
onReturn: static list => list.Clear()
```

把列表清空，保证下一个人拿到的是干净的空列表，不会吃到上一位的菜汤（残留数据）。

### 14.11 借书卡：自动归还（`using` 租赁）

```csharp
using var lease = pool.RentLease(out var list);
```

就像借书卡：借的时候登记，**走到图书馆门口自动还**。哪怕你中途摔了一跤（代码抛异常）或者提前溜了（提前 `return`），也不会忘记还。

旧代码是手写"我借了、我记得还"，一旦中间出错，那本书（列表）就永远丢了。

### 14.12 员工下班了怎么办（终结器）

临时工（临时创建的线程）下班走了，柜子里还有碗，不能让他一起带走。

"终结器"就是**离职清柜流程**：线程一死，系统自动把它的柜子交还给大仓库。

有个细节：**负责清柜的人全店只有一个**，所以他不能干重活——代码里规定这条路上不执行任何用户回调（比如"要不要扔掉"的自定义逻辑），只做最简单的搬碗动作。

### 14.13 实在放不下就扔掉（`onDiscard`）

柜子满、仓库也满 → 把碗丢掉，而不是报错。

因为"还碗失败"的损失很小（顶多下次重新分配一个），但要是抛异常，可能把正在正常运行的剔除逻辑炸掉，得不偿失。

### 14.14 一句话总结

| 概念 | 大白话 |
| --- | --- |
| 对象池 | 用完的碗放回碗架，别买新的 |
| 竞态条件 | 两个人同时伸手，撞手了 |
| 可见性 | 你改了，别人还没看见 |
| 锁 | 门口排队，安全但慢，还可能死锁 |
| 原子操作 | 小到不可能被插队的动作（数数不会数错） |
| CAS | "确认没变我才改"，失败了就重试 |
| 无锁 | 谁都不用等谁，谁被冻住都不影响别人 |
| `[ThreadStatic]` | 每个线程一块自己的白板 |
| 线程本地缓存 | 每个窗口自己的小碗柜，不用抢 |
| 全局池 | 共用的大仓库，负责跨窗口周转 |
| LIFO | 先拿刚放回去的那个，因为还热乎（缓存命中） |
| `onReturn` | 放回柜子前先洗干净（重置状态） |
| 租赁 `using` | 借书卡，走到门口自动还 |
| 终结器 | 员工离职时自动清柜，把碗交回仓库 |
| 统计快照 | 记录"借了几次、买了几次、命中率多少"，方便排查 |
