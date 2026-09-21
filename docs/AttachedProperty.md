# AttachedProperty 设计说明

> 相关文件
> - 实现：`SharedProject/SharedSource/Collections/AttachedProperty.cs`（`AttachedProperty<T>` / 内部 `Slot`）
> - 创建与持有：`ClientProject/ClientSource/Culling/PluginClient.cs`（`entityVisibleExtents`、`entityHull`、`isEntityCulled`；前者经 `Plugin.EntityVisibleExtents` 只读暴露）
> - 写入方：`Structure.IsVisible` 经 `SharedProject/SharedSource/Patching/Patches.cs` 的 transpiler 调
>   `Plugin.CacheStructureVisibleExtents`（只写 `entityVisibleExtents`）
> - 读取方：`SharedProject/SharedSource/Patching/Patches.cs`（渲染谓词、`Character.Draw`，读的是 `isEntityCulled`）、`SharedProject/SharedSource/Rendering/DebugDrawing.cs`（调试绘制）、`ClientProject/ClientSource/Culling/PluginClient.cs`（结构剔除分支读 `entityVisibleExtents`）
> - 相邻设施：[`ObjectPool.md`](./ObjectPool.md)、[`PooledLinkedList.md`](./PooledLinkedList.md)（同属 `Collections/` 层）
>
> 环境：.NET 8 / `Nullable=enable` / `LangVersion=latest`，无新增依赖（只用 BCL 的
> `ConditionalWeakTable<TKey,TValue>` 与 `Volatile` / `Interlocked`）。

---

## 目录

1. [背景：旧实现的问题](#1-背景旧实现的问题)
2. [重设计目标](#2-重设计目标)
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
14. [附录 A：小白版 —— 演唱会手环比喻](#14-附录-a小白版--演唱会手环比喻)

> 只想快速理解设计意图的读者，可以直接跳到最后的[附录 A](#14-附录-a小白版--演唱会手环比喻)；
> 需要精确语义、参数与并发论证的读者，从第 1 节顺序阅读。

---

## 1. 背景：旧实现的问题

旧实现（72 行）是「一张 `ConditionalWeakTable<object, StrongBox<T>>` + 一个整表重置方法」：

```csharp
private readonly ConditionalWeakTable<object, StrongBox<T>> storage = new();
private T defaultValue;
public ref T DefaultValue => ref defaultValue;

public T GetValue(object obj) => storage.GetValue(obj, newBox).Value!;   // ← 问题 1

public ref T GetValueRef(object obj, out bool isNew)                     // ← 问题 3、4
{
    isNew = false;
    if (!storage.TryGetValue(obj, out var box))
    {
        storage.Add(obj, box = newBox(obj));   // ← 检查与写入之间的窗口期
        isNew = true;
    }
    return ref box.Value!;
}

public void ResetValues()                                               // ← 问题 2
{
    foreach (var box in storage) { box.Value.Value = DefaultValue; }
}
```

逐条问题如下：

| # | 问题 | 说明 |
| --- | --- | --- |
| 1 | **读路径会建表** | `GetValue` 走的是 `storage.GetValue(obj, newBox)`——键不存在时会**插入**一个 `StrongBox<T>`。而渲染谓词（`Patches.cs` 的 `Submarine.Draw*` 前缀与 `Character.Draw`）对**每个可见实体、每一帧**都要读一次，于是弱表只增不减，随着游戏时长持续膨胀 |
| 2 | **整表重置是 O(n) 的数据写** | `ResetValues()` 遍历整张弱表、逐条把默认值写回 box。剔除每 `CullingInterval`（默认 0.05s）一轮，等于每秒约 20 次全表遍历 + 全表写 |
| 3 | **`GetValueRef(obj, out isNew)` 不是原子的** | `TryGetValue` 失败与 `Add` 之间存在窗口期（TOCTOU）。同一把键并发进入时，第二个线程的 `Add` 会抛 `ArgumentException`。当前恰好没有「同一实体被两个分片线程同时取引用」的调用点，所以只是「没炸」，而不是「安全」 |
| 4 | **「取引用 + 自己判断是否新建」易错** | 调用方必须记得在 `isNew == true` 时填充初始值，漏写就留下一个半初始化条目；`entityVisibleExtents` 与 `entityHull` 两个调用点都要重复这套样板 |
| 5 | **公开面远大于实际需要** | `IAttachedProperty`、`IAttachedProperty<T>`、`Remove(object)`、单参 `GetValueRef(object)`、`ref T DefaultValue` 全仓库**零引用**（已用引用检索确认） |
| 6 | **零文档** | 既没有 XML 注释，也没有任何地方说明它的线程契约——「它能不能在工作线程上写」只能靠读源码猜 |

---

## 2. 重设计目标

| 维度 | 结论 |
| --- | --- |
| 语义 | **完全保留**：以对象为键、键为弱引用、整表可失效；未写入的键读为默认值 |
| 性能目标 | 读路径**零分配且不建表**；整表失效 **O(1) 且零分配** |
| 正确性目标 | 「取或建」改为**原子**操作，消除 TOCTOU 抛异常的窗口期 |
| 并发目标 | 不是「加锁变安全」，而是**把并发契约写清楚**，并让竞态的失败方向落在无害的一侧 |
| API 兼容性 | **允许破坏性变更**：`GetValueRef(obj, out isNew)` → `GetOrAdd(key, factory)`，`ResetValues()` → `InvalidateAll()`，同步修改调用点 |
| API 面 | 只保留有调用点的成员；删除 2 个接口与 3 个死成员 |
| 文档 | 即本文件 |

---

## 3. 总体架构

整套设计只建立在两个判断上：

> **判断一：失效应该是「换代际」，而不是「改数据」。**
> **判断二：读取不应该产生条目。**

```
                        AttachedProperty<T>
      ┌──────────────────────────────────────────────────────────┐
      │  epoch = 3                     ← 整表代际，失效只动它      │
      │  defaultValue = false                                    │
      │                                                          │
      │  ConditionalWeakTable<object, Slot> slots                │
      │      key(实体) ·····弱引用····► Slot { Value, Epoch }      │
      └──────────────────────────────────────────────────────────┘

   GetValue(k)      查表 ─► 命中且 Slot.Epoch == epoch ? Slot.Value : defaultValue   （不插入）
   SetValue(k, v)   取或建 ─► Value = v; Epoch = epoch                              （release 发布）
   GetOrAdd(k, f)   取或建 ─► Slot.Epoch != epoch ? Value = f(k); Epoch = epoch : 命中
   InvalidateAll()  epoch++                                                          （O(1)、零分配）
   Clear()          slots.Clear(); epoch++                                           （O(条目数)）
```

三个设计要点：

- **代际戳放在条目里**，而不是靠「整表重建」：`ConditionalWeakTable.Clear()` 会替换内部容器并留下一个待终结的容器对象，若每个剔除周期都调用一次，等于每秒制造 20 个可终结对象；代际方案零分配。
- **读路径不物化**：`GetValue` 用 `TryGetValue`，命中且代际有效才返回 `Slot.Value`，否则直接返回 `defaultValue`，绝不插入条目。
- **条目随键消亡**：弱表由运行时维护，键被回收时条目自动消失，本类型不需要任何清理逻辑（也不需要终结器）。

---

## 4. API 一览

### 4.1 成员

```csharp
public sealed class AttachedProperty<T>
{
    public AttachedProperty(T defaultValue = default!);
    public static AttachedProperty<T> Create(T defaultValue = default!);

    public T DefaultValue { get; }

    public T GetValue(object obj);
    public void SetValue(object obj, in T value);
    public ref T GetOrAdd<TKey>(TKey key, Func<TKey, T> factory) where TKey : class;

    public void InvalidateAll();
    public void Clear();
}
```

| 成员 | 说明 |
| --- | --- |
| `AttachedProperty(T)` / `Create(T)` | 构造与工厂，二者等价。`Create` 保留是因为 3 个创建点都写成字段初始化器，`Create` 在那里可读性更好 |
| `DefaultValue` | 旧版是 `ref T`，**改为只读属性**——没有任何调用点需要改默认值 |
| `GetValue(object)` | 热读路径。命中且代际有效返回值，否则返回 `DefaultValue`；**不插入条目、零分配** |
| `SetValue(object, in T)` | 写入并盖当前代际。并行热路径，每个键一个写者 |
| `GetOrAdd<TKey>(TKey, Func<TKey,T>)` | **惰性初始化入口**，取代 `GetValueRef(obj, out isNew)`。条目缺失或已失效时调用 `factory` 计算初始值，返回条目内值的引用 |
| `InvalidateAll()` | O(1) 逻辑失效：所有键立刻重新读为 `DefaultValue`，不遍历弱表 |
| `Clear()` | 物理丢弃全部条目。用于关卡/场景切换，不用于每轮 |

### 4.2 与旧 API 的对应关系

| 旧 | 新 | 原因 |
| --- | --- | --- |
| `GetValue(object)` | 同名，**语义收紧** | 不再插入条目 |
| `SetValue(object, in T)` | 同名 | 加入代际发布语义 |
| `GetValueRef(object, out bool isNew)` | `GetOrAdd<TKey>(TKey, Func<TKey,T>)` | 「取引用 + 自己判断是否新建 + 自己填初值」三段式收敛为一次调用，且类型化工厂免去 `(Structure)obj` 强转 |
| `ResetValues()` | `InvalidateAll()` | 新语义是「逻辑失效」而非「逐条写回默认值」，旧名字会误导 |
| `DefaultValue`（`ref T`） | `DefaultValue`（`T`） | 无人写入默认值 |
| `GetValueRef(object)`、`Remove(object)` | 删除 | 零引用 |
| `IAttachedProperty`、`IAttachedProperty<T>` | 删除 | 零引用（`Plugin.IsEntityCulled` 暴露的是具体类型 `AttachedProperty<bool>`，从未面向接口编程） |

### 4.3 用法示例

```csharp
// 每轮剔除的标记：默认 false，并行写，主线程读
private static readonly AttachedProperty<bool> isEntityCulled = new(defaultValue: false);

isEntityCulled.SetValue(entity, true);        // 工作线程
if (isEntityCulled.GetValue(entity)) { ... }  // 渲染谓词 / 调试绘制
isEntityCulled.InvalidateAll();               // 下一轮开始前，主线程，O(1)

// 惰性缓存：只在首次（或失效后）计算一次
ref Hull? hull = ref entityHull.GetOrAdd(structure, static s => Hull.FindHull(s.WorldPosition));

// 由游戏侧发布、剔除侧只读；发布的是游戏自己算出的两个角点 (max, min)，存的是绝对世界坐标
Plugin.CacheStructureVisibleExtents(structure, max, min);         // Structure.IsVisible 内由 transpiler 注入
RectangleF extents = Plugin.EntityVisibleExtents.GetValue(structure);
```

---

## 5. 数据结构

### 5.1 `Slot`

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Value` | `T` | 附加的值。`T` 为可空类型时允许为 `null`（`entityHull` 就是 `Hull?`） |
| `Epoch` | `long` | 该值写入时的代际。新建的 `Slot` 为 `0`，而实例代际从 `1` 起，因此「从未写入」永远不会被误判为有效 |

`Slot` 同时承担两件事：存值，以及作为 `Value` 的**发布标志**。`Epoch` 在 `Value` 之后写、在 `Value` 之前读，于是「读到当前代际」就意味着「读到的值属于当前代际」。

### 5.2 实例字段

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `slots` | `ConditionalWeakTable<object, Slot>` | 键为弱引用。`TValue` 必须是引用类型，这也是 `Slot` 存在的原因——不能直接把 `T`（可能是 `bool`、`RectangleF` 等值类型）放进弱表 |
| `defaultValue` | `T` | 只读的默认值 |
| `epoch` | `long` | 当前代际。用 `long` 而非 `int`：`InvalidateAll()` 在每轮剔除都自增一次，`int` 在极端长时间运行下存在回绕风险，而回绕会让过期条目被误判为有效 |

### 5.3 为什么键用弱引用

附加属性的键是游戏实体（`Hull`、`Item`、`Structure`、`Character`）。这些对象的生命周期由游戏决定，插件无权决定何时释放。如果用普通 `Dictionary<object, T>`，插件会**替游戏保活每一个曾经被剔除过的实体**，在关卡切换后造成整张地图的实体泄漏。弱表把「条目随键消亡」交给运行时，本类型不需要订阅任何销毁事件。

---

## 6. 核心流程

### 6.1 `GetValue(obj)`

```
slots.TryGetValue(obj, out slot)?
   ├─ 否 ─────────────────────────────► 返回 defaultValue          （不插入）
   └─ 是 ─► slot.Epoch == Volatile.Read(epoch)?
              ├─ 否 ──────────────────► 返回 defaultValue          （条目过期）
              └─ 是 ──────────────────► 返回 slot.Value
```

### 6.2 `SetValue(obj, value)`

```
current = Volatile.Read(ref epoch)      ← 先采代际（顺序有意为之，见 7.3）
slot    = GetOrCreateSlot(obj)          ← 原子取或建
slot.Value = value                      ← 先写值
Volatile.Write(ref slot.Epoch, current) ← 后发布代际（release）
```

### 6.3 `GetOrAdd(key, factory)`

```
current = Volatile.Read(ref epoch)
slot    = GetOrCreateSlot(key)
if (slot.Epoch != current)              ← 条目缺失时代际为 0，必然进入
{
    slot.Value = factory(key);
    Volatile.Write(ref slot.Epoch, current);
}
return ref slot.Value
```

`factory` 在**条目缺失或已失效**时执行，即「每个条目每代最多一次」（丢竞态时可能两次，见 7.4）。

### 6.4 `GetOrCreateSlot(key)`（私有慢路径）

```
slots.TryGetValue(key, out slot)?  是 ─► 直接返回            （快路径，无锁、无分配）
                                  否
Slot created = new();
slots.TryAdd(key, created)?        是 ─► 返回 created        （原子插入）
                                  否
slots.GetValue(key, _ => new Slot())                          （输掉竞态：取胜者的条目）
```

关键点是**用 `TryAdd` 而不是 `TryGetValue` + `Add`**：前者原子，两个线程抢同一把键时一定有一个成功、另一个拿到胜者的条目；后者则会让后者抛 `ArgumentException`。

### 6.5 `InvalidateAll()` 与 `Clear()`

```
InvalidateAll()  ─► Interlocked.Increment(ref epoch)   // 只有一个自增
Clear()          ─► slots.Clear(); Interlocked.Increment(ref epoch)
```

`Clear()` 里额外自增代际是防御性的：`Clear()` 与写入竞态时，写者可能带着旧代际把条目「复活」，而代际前移会让这类条目立即过期。

---

## 7. 并发正确性论证

### 7.1 前提：弱表本身是线程安全的

`ConditionalWeakTable<TKey,TValue>` 的所有公开操作（`TryGetValue` / `TryAdd` / `GetValue` / `Clear` / `Remove`）内部自带同步，可以在任意线程上并发调用。因此本类型**不需要自己的锁**——它只额外维护一个 `long epoch`，而所有对代际的操作都是原子的（`Volatile.Read` / `Volatile.Write` / `Interlocked.Increment`）。

### 7.2 值与其代际的可见性

写入方向（`SetValue` / `GetOrAdd`）：`Slot.Value` 在前、`Slot.Epoch` 在后，后者用 `Volatile.Write`（**release**）。
读取方向（`GetValue` / `GetOrAdd`）：`Slot.Epoch` 在前且用 `Volatile.Read`（**acquire**），`Slot.Value` 在后；
实例上的 `epoch` 同样一律用 `Volatile.Read`。

这正是标准的 release/acquire 配对：**读到新代际 ⇒ 一定读到与之配对的 `Value`**，不会出现「代际已更新、值还是上一代的」。

反方向的竞态（读到代际后，写者刚好把 `Value` 改成同一代的另一个值）是允许的：`bool` 场景下两个写者写的都是 `true`；退一步说，读到同代的任意一个值都不会破坏语义。

### 7.3 与 `InvalidateAll()` 竞态时的失败方向（重要）

`SetValue` **先采代际、后写值**，这个顺序不是随手写的：

- 若在采代际之后发生了 `InvalidateAll()`，写入会盖上**旧**代际 ⇒ 条目立即过期 ⇒ 该实体本轮**未被剔除**（多渲染一帧）。
- 若顺序反过来（先写值、后采代际），就可能盖上**新**代际 ⇒ 标记泄漏到下一轮 ⇒ 一个**玩家看得见的实体被隐藏**。

两种错误的代价不对称，所以设计主动站到了无害的一侧。契约上仍然要求 `InvalidateAll()` 只在主线程、且在该轮并行写入**开始之前**调用（见第 11 节），7.3 只是为「万一违约」兜底。

### 7.4 `factory` 可能被执行两次

`GetOrAdd` 在「条目缺失/过期」时运行工厂，而两个线程可能同时判定某个键「需要计算」。`ConditionalWeakTable.TryAdd` 保证只有一个值被存下，但**两个线程都算过一次**是可能的，落败者的结果被丢弃。

因此契约要求 `factory` **只做「由 key 计算 value」这一件事**，不得有副作用（现在唯一的工厂是 `entityHull` 的 `Hull.FindHull`，它是查表）。

### 7.5 同一实体被多个分片线程读写

`Cull<T>` 在 PLINQ 分区上并行执行，`isEntityCulled.SetValue(entity, true)` 由工作线程写入，而同一轮内 `Cull<T>` 也会**读取** `isEntityCulled.GetValue(itemHull)`（`item` 落在 A 分片、其所属 `Hull` 落在 B 分片）。也就是说：**同一轮内，某个实体可能读到同轮其他线程刚写下的标记**。

这是改造**前后一致**的既有性质（旧实现同样如此，因为旧 `GetValue` 也是直接查弱表），本次不改变它。它的实际影响是「剔除判定会利用同轮已经算出的结果」，属于期望行为而非缺陷；需要知道的是**剔除结果依赖于分片调度**，因此调试时同一场景两轮的剔除数量可能有微小差异。

### 7.6 为什么不是 `HashSet<Entity>` / `Dictionary`

| 候选 | 否决理由 |
| --- | --- |
| `HashSet<Entity>` + 每轮 `Clear()` | 并行写入需要并发集合（`ConcurrentDictionary`）或加锁；每轮 `Clear()` 需要遍历/重建，且会让下一轮的每个写入重新引发扩容 |
| `Dictionary<object, T>` | 强引用键 ⇒ 实体泄漏（见 5.3） |
| 每个实体上挂字段 | 游戏实体是第三方类型，插件无法为其添加字段；且会把「剔除状态」这一临时概念固化进游戏对象模型 |

---

## 8. 与调用点的集成

`AttachedProperty<T>` 在插件里只有 3 个实例，全部声明在 `ClientProject/ClientSource/Culling/PluginClient.cs`：

| 实例 | 类型 | 默认值 | 角色 |
| --- | --- | --- | --- |
| `entityVisibleExtents` | `RectangleF` | `default` | `Structure` 的可见范围，由游戏 `Structure.IsVisible` 通过 transpiler 发布（写：`Plugin.CacheStructureVisibleExtents`，绝对世界坐标；读：`Plugin.EntityVisibleExtents.GetValue` 或同文件私有字段）；`Clear()` 在 `TryClearAll` |
| `entityHull` | `Hull?` | `null` | `Structure` 所属 Hull 的缓存（`GetOrAdd` 惰性查询，`Clear()` 在 `TryClearAll`） |
| `isEntityCulled` | `bool` | `false` | 本轮剔除结果，经 `Plugin.IsEntityCulled` 对外暴露 |

### 8.1 创建（`PluginClient.cs`）

```csharp
private static AttachedProperty<RectangleF> entityVisibleExtents = AttachedProperty<RectangleF>.Create();
private static AttachedProperty<Hull?> entityHull = AttachedProperty<Hull?>.Create();
private static AttachedProperty<bool> isEntityCulled = AttachedProperty<bool>.Create(false);
```

### 8.2 每轮失效（`CullEntities`）

```csharp
isEntityCulled.InvalidateAll();   // 主线程；紧随其后的就是 PLINQ 并行段
```

位置很关键：它在 `Partitioner.Create(...).AsParallel()` **之前**，满足 7.3 的契约。

### 8.3 发布与惰性缓存（`Structure.IsVisible` → `Cull<T>` 的结构分支）

```csharp
// 发布侧：Structure.IsVisible 在算 extents = max - min 处，由 Patches.cs 的 transpiler 注入
Plugin.CacheStructureVisibleExtents(structure, max, min);

// 读取侧（Cull<T> 的结构分支）
entityAABB = entityVisibleExtents.GetValue(structure);

ref Hull? structureHull = ref entityHull.GetOrAdd(structure, static s => Hull.FindHull(s.WorldPosition));
```

两点说明：

- `entityVisibleExtents` 不再由剔除侧计算：范围由游戏自己的 `Structure.IsVisible` 算出（旋转包围盒 + 装饰精灵扩展 + 塌陷偏移，世界坐标），transpiler 只负责转手发布，因此 `EntityBounds.CalculateFixed` 被删除、读取方也不再 `Offset(DrawPosition)`。发布节奏跟随游戏可见性剔除（相机移动 ≥ `Submarine.CullMoveThreshold` 或约每 `Submarine.CullInterval` 秒），比"整个关卡只算一次"更新。
- 存入的是**绝对世界坐标**：`CacheStructureVisibleExtents` 把收到的两个角点归一成 `RectangleF(min.X, max.Y, |Δx|, |Δy|)` 直接写入。角点以游戏的非插值 `WorldPosition` 为基准，与读取侧比较的 `hull.WorldRect` 同基准；代价是范围最多滞后一轮游戏可见性剔除。因此 `CacheStructureVisibleExtents` 只应在 `Structure.IsVisible` 内调用。
- 条目缺失（从未发布，或刚被 `Clear()`）时 `GetValue` 返回 `default(RectangleF)`，也就是一个零矩形——它照常参与判定，而不是跳过该结构。`entityHull` 仍走 `GetOrAdd`：工厂写成 `static` lambda、无捕获（Roslyn 缓存在静态字段里，每次调用不分配委托），且可以返回 `null`（`Hull.FindHull` 找不到时），调用点保留了 `structureHull != null` 判空。

### 8.4 并行写入与读取

| 位置 | 操作 |
| --- | --- |
| `Cull<T>` | `isEntityCulled.SetValue(entity, true)`（工作线程，每个键一个写者） |
| `Cull<T>` | `isEntityCulled.GetValue(itemHull / structureHull)`（工作线程，见 7.5） |
| `Patches.cs`（`Submarine.Draw*` 前缀） | `!Plugin.IsEntityCulled.GetValue(entity)`（主线程，每实体每帧） |
| `Patches.cs`（`Character.Draw` 前缀） | `!Plugin.IsEntityCulled.GetValue(__instance)`（主线程） |
| `Patches.cs`（`Structure.IsVisible` transpiler） | `Plugin.CacheStructureVisibleExtents(structure, max, min)`（主线程，游戏可见性剔除节奏） |
| `DebugDrawing.cs` | 调试绘制读取剔除状态与已发布的可见范围（主线程） |

### 8.5 清理（`TryClearAll()`）

```csharp
entityVisibleExtents.Clear();
entityHull.Clear();
isEntityCulled.Clear();
```

实体被销毁 / 关卡切换时物理丢弃条目——这一路径不要求 O(1)，用 `Clear()` 是刻意的选择。

---

## 9. 性能特性

| 操作 | 调用频率 | 旧实现 | 新实现 |
| --- | --- | --- | --- |
| `GetValue` | 每可见实体每帧（渲染谓词） | 弱表查找；未命中时**插入条目 + 分配 `StrongBox<T>`** | 弱表查找 + 一次代际比较；**不插入、零分配** |
| `SetValue` | 每个被剔除实体每轮 | 弱表查找；未命中时插入 | 弱表查找（快路径）+ 两次原子写 |
| 每轮重置 | 约 20 次/秒 | **O(条目数)** 遍历 + 逐条写默认值 | **O(1)** 一次自增，零分配 |
| `GetOrAdd` | 每个 `Structure` 每轮（仅命中路径） | `TryGetValue` + 可能 `Add`（非原子，可能抛异常） | `TryGetValue` 快路径；未命中走 `TryAdd`，原子 |
| `Clear` | 仅场景切换 | O(条目数) | O(条目数)（弱表自身成本）+ 一次自增 |

复杂度小结：所有单条目操作 O(1)；`InvalidateAll()` O(1)；`Clear()` 与条目数成正比，但只在场景切换时调用。

内存小结：弱表只保留**被 `SetValue` / `GetOrAdd` 触碰过**的键（旧实现还会保留所有被**读过**的键），并且这些条目会随实体的回收而消失。

---

## 10. 基础概念小抄

### 10.1 强引用与弱引用

普通字段/集合里存的对象引用是**强引用**：只要引用还在，GC 就不会回收被引用者。**弱引用**（`WeakReference` / `WeakReference<T>` / `ConditionalWeakTable`）不阻止回收，被引用者被回收后引用自动变为「已失效」或条目自动消失。

附加属性必须弱引用键，否则插件会替游戏保活实体（见 5.3）。

### 10.2 `ConditionalWeakTable<TKey,TValue>`

它是一张「键弱、值随键存活」的哈希表，等价于「把值挂在键对象上」。要点：

- `TKey` 与 `TValue` 都必须是**引用类型**（所以值类型要放进 `Slot` 这样的包装类）。
- 键被回收后条目自动删除，无需手动清理，也没有终结器负担。
- 所有操作线程安全；有 `TryGetValue` / `TryAdd` / `GetValue(key, callback)` / `GetOrCreateValue` / `Remove` / `Clear`。
- **没有** O(1) 的 `Count`（要数只能遍历，所以本类型不提供容量统计）。

### 10.3 代际（epoch）失效

给每个条目打一个「写入时的版本号」，整表失效时只把全局版本号 +1。于是「版本号 != 当前版本号」的条目读起来等同于不存在。

好处：失效从**数据操作**（遍历所有条目、逐条写默认值）降级为**元数据操作**（一个整数自增），复杂度从 O(n) 变成 O(1)，且零分配。代价是过期条目仍占着内存直到被重写或 `Clear()`，这在「每轮都会重写大量标记」的场景下反而正好（条目会被就地复用，不需要重建）。

同一手法也用于「缓存失效」「高频重建的位图」等场景，术语上常叫 generation / version / epoch。

### 10.4 TOCTOU

Time-Of-Check-To-Time-Of-Use：先检查条件、再依据检查结果行动，但两者之间条件可能已经变了。旧代码 `if (!TryGetValue(...)) { Add(...); }` 就是典型 TOCTOU——检查通过之后、`Add` 之前，另一个线程可能已经插入了同一个键。

修法是**把检查与行动合并成一个原子操作**（这里是 `TryAdd`），而不是在两步之间加锁。

### 10.5 release / acquire 语义

现代 CPU 与编译器都可能重排内存访问。**release 写**表示「此前的所有写，在本次写之后对其他线程可见」；**acquire 读**表示「读到本次写的线程，能看到此前 release 之前的那些写」。C# 里对应 `Volatile.Write` / `Volatile.Read`（以及 `Interlocked` 系列）。

本类型用它保证：**看到新代际的线程一定看到与之配对的值**（见 7.2）。

### 10.6 惰性初始化与工厂

「第一次用到时才算」的常见写法是「检查是否存在 → 不存在则创建」，但调用方要自己处理「是否新建」的返回值，容易漏写。`GetOrAdd(key, factory)` 把这段样板收进类型内部：**调用方只提供「怎么算」，不关心「是不是第一次」**。

---

## 11. 契约与注意事项

| 契约 | 说明 |
| --- | --- |
| 键必须为引用类型 | `GetValue` / `SetValue` 的形参是 `object`；`GetOrAdd<TKey>` 有 `where TKey : class` 约束，避免装箱 |
| `GetValue` 不插入 | 读一个从未写过的键不会创建条目，也不会分配 |
| `InvalidateAll()` / `Clear()` 只在主线程、且只在并行写入**开始之前**调用 | 违约的后果见 7.3（会朝无害方向退化，但不保证语义） |
| `SetValue` 允许一对键多线程写 | 不同键之间互不影响；同一键同时写不崩溃，但胜者未定义 |
| `factory` 必须无副作用 | 丢竞态时可能执行两次（见 7.4） |
| `GetOrAdd` 返回的引用不可长期保存 | 引用指向条目内部，仅在「键存活 + 条目未被失效/丢弃」期间有效。调用点都是立即使用 |
| 条目随键自动消亡 | 不要在条目上挂「必须被显式清理」的资源（如需清理，自行实现终结或显式 `Remove` 语义） |
| 不提供容量统计 | `ConditionalWeakTable` 没有 O(1) 的 `Count`，本类型不假装提供 |

---

## 12. 观测与调优

- **本轮是否真的在被复用**：`entityHull` 是纯缓存，若 `TryClearAll()` 被频繁触发（例如关卡反复切换），其收益会退化——此时应检查 `isCullingStateDirty` 的置位条件，而不是调整本类型。`entityVisibleExtents` 不同：它由游戏按 `Structure.IsVisible` 的节奏重新发布；被清空后，这些结构在重新发布之前读到的是零矩形（最多约 `Submarine.CullInterval` 秒），随后自动恢复。
- **剔除数量波动**：见 7.5，同一场景两轮的 `Cull(Hull)` / `Cull(NonHull)` 计数可能有微小差异，属正常现象（分片调度影响同轮可见的结果）。可从 `Plugin.DebugLoggingEnabled` 的性能日志观察。
- **`InvalidateAll()` 的成本**：如果不借助性能计数器，很难在采样中看到它的开销——这正是设计目标（旧实现的 `ResetValues()` 在条目多时可以在火焰图上看到）。
- **不要为了「省一次自增」而累积多轮失效**：代际是 `long`，不存在回绕压力；相反，漏掉一次 `InvalidateAll()` 会让上一轮的剔除结果继续生效，属于逻辑错误。

---

## 13. 术语速查表

| 术语 | 含义 |
| --- | --- |
| 附加属性（attached property） | 把「某个对象的额外状态」存在对象之外的表里，而不是往对象上加字段 |
| 弱引用（weak reference） | 不阻止被引用者被 GC 回收的引用 |
| `ConditionalWeakTable` | BCL 提供的「键弱引用」哈希表，条目随键消亡 |
| `Slot` | 本类型的条目：`Value` + `Epoch` |
| 代际 / epoch / generation | 整表共享的版本号；失效 = 版本号前移，旧代际条目视为不存在 |
| 物化（materialize） | 读取时顺手创建条目；本类型**不**这么做 |
| TOCTOU | 检查与使用之间条件发生变化导致的竞态 |
| release / acquire | 内存序语义：release 发布此前的写，acquire 读取被发布的写 |
| 惰性初始化（lazy init） | 首次使用时才计算值 |
| 失效（invalidate） | 让已有值不再生效；本类型是逻辑失效，不擦除数据 |

---

## 14. 附录 A：小白版 —— 演唱会手环比喻

### 14.1 场景

把 `AttachedProperty<T>` 想成一场**巡回演唱会**的入场管理：

- **观众** = 键（游戏实体：Hull、Item、Structure、Character）
- **手环** = 条目（`Slot`）
- **手环上印的信息** = 值（`Value`）
- **手环上印的场次号** = 代际（`Epoch`）
- **门口挂的「当前场次」牌子** = 实例的 `epoch`

### 14.2 检票：只看手环，不发手环

保安（渲染谓词）每帧都要问每一位观众：「你现在戴的是**本场次**的手环吗？」

- 观众没戴手环 → 不算数（读 `defaultValue`）
- 观众戴的是**上一场**的手环 → 也不算数（代际不匹配）
- 戴的是本场次手环 → 按手环上的信息处理

关键规矩：**保安绝不给没戴手环的观众补发手环**。这条规矩对应「读路径不建表」——否则每个进场的人都会留下一个手环，一天下来仓库堆满。

### 14.3 换场：只换牌子，不撕手环

旧的实现相当于「换场时必须派人把每一位观众的手环撕下来」——观众越多越慢，而且每秒要撕 20 遍。

新实现只做一件事：**把门口那块「当前场次」的牌子换成下一场**。

于是所有还戴着旧手环的观众，在保安眼里自动变成「没戴有效手环」——不需要碰他们任何人。这就是 `InvalidateAll()`：**O(1)、零分配**。

代价是旧手环还留在观众手上，直到他们被重新认定为「本场次」（重新盖上章）或者干脆离场。这正好是我们想要的：标记本来每轮就要重写一遍，就地改印比回收再发更省。

### 14.4 观众离场：手环自动消失

观众走了（实体被 GC 回收），他手上的手环也就跟着人间蒸发——这是弱表（`ConditionalWeakTable`）的功劳，主办方（插件）不需要安排任何人去回收。

反过来说，如果用手环名单（普通 `Dictionary`）来管，主办方就必须**替每位观众保管他们本人**，散场后一个人也走不掉：这就是「强引用键导致实体泄漏」。

### 14.5 补发手环时顺手把信息写上

有些观众需要「本场次的手环 + 印好的座位信息」。旧流程是三步：

1. 问主办方「给我手环，顺便告诉我这是新发的吗？」
2. 如果是新发的，自己想办法把座位信息印上去
3. 忘了第 2 步 → 得到一个空白手环

新流程是一步：**「给我手环，如果没有就照这个方法印」**——也就是 `GetOrAdd(key, factory)`。工厂方法只负责「怎么算」，不需要知道「是不是第一次」。

### 14.6 两个检票员同时给同一个人补发

如果两个人同时发现某位观众没有手环、同时去发，旧流程会**直接报错**（`ArgumentException`：这个键已经存在了）。

新流程是「谁先递上去算谁的」：一个人成功，另一个人拿到前者发的那只手环，两边都不会出错——这就是用 `TryAdd` 取代「先查再插」的意义（TOCTOU）。

### 14.7 盖章的顺序为什么不能反

给观众盖「本场次」章之前，**先看好现在挂的是哪一场的牌子**，再动手盖章。

- 顺序对：万一在你看牌子之后、盖章之前换了场次，你盖的是**旧场次**章 → 这只手环当场作废 → 观众本轮被多渲染一帧（无害）
- 顺序反：先盖章、后看牌子，可能盖上**新场次**章 → 手环混进下一场 → **一个明明看得见的人被挡在门外**（有害）

两种失手的代价不对称，所以设计主动选了无害的那一边（见 7.3）。

### 14.8 一句话总结

> 手环挂在观众手上、场次号印在手环上、门口只挂一块「当前场次」的牌子——
> **换场只需要换牌子，检票不需要发手环，观众走了手环自己消失。**
