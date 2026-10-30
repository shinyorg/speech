using System.Runtime.CompilerServices;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// Thrown to a subscriber that fell so far behind that a lifecycle event could not be queued.
/// </summary>
public sealed class BroadcastOverflowException()
    : Exception("The subscriber fell too far behind and was disconnected");

/// <summary>
/// Hot fan-out over <see cref="IAsyncEnumerable{T}"/>. Each subscription has its own bounded queue so a slow
/// subscriber never blocks the publisher or other subscribers. When a queue is full, the oldest coalescable
/// item is dropped to make room; if there is none, the subscriber is completed with a
/// <see cref="BroadcastOverflowException"/>, because lifecycle items are never dropped.
/// </summary>
sealed class BroadcastChannel<T>(Func<T, bool> isCoalescable, int capacity = 256)
{
    readonly Lock sync = new();
    readonly List<Subscriber> subscribers = [];
    bool completed;

    public int SubscriberCount
    {
        get { lock (this.sync) return this.subscribers.Count; }
    }

    /// <summary>
    /// Subscribes when enumeration starts. <paramref name="snapshot"/> is taken atomically with the
    /// registration and is always the first item, so nothing is missed between the two.
    /// </summary>
    public async IAsyncEnumerable<T> Subscribe(Func<T>? snapshot = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sub = new Subscriber(capacity);
        lock (this.sync)
        {
            if (snapshot != null)
                sub.Queue.AddLast(snapshot());

            if (this.completed)
                sub.Completed = true;
            else
                this.subscribers.Add(sub);
        }
        sub.Signal.Release();

        try
        {
            while (true)
            {
                var hasItem = false;
                var item = default(T)!;
                lock (sub.Sync)
                {
                    if (sub.Queue.Count > 0)
                    {
                        item = sub.Queue.First!.Value;
                        sub.Queue.RemoveFirst();
                        hasItem = true;
                    }
                    else if (sub.Error != null)
                    {
                        throw sub.Error;
                    }
                    else if (sub.Completed)
                    {
                        break;
                    }
                }

                if (hasItem)
                {
                    yield return item;
                }
                else
                {
                    try
                    {
                        await sub.Signal.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (this.sync)
                this.subscribers.Remove(sub);
        }
    }

    public void Publish(T item)
    {
        Subscriber[] subs;
        lock (this.sync)
        {
            if (this.completed)
                return;

            subs = this.subscribers.ToArray();
        }

        var coalescable = isCoalescable(item);
        foreach (var sub in subs)
        {
            lock (sub.Sync)
            {
                if (sub.Completed || sub.Error != null)
                    continue;

                if (sub.Queue.Count >= sub.Capacity && !this.MakeRoom(sub, coalescable))
                    continue;

                sub.Queue.AddLast(item);
            }
            sub.Signal.Release();
        }
    }

    bool MakeRoom(Subscriber sub, bool incomingCoalescable)
    {
        for (var node = sub.Queue.First; node != null; node = node.Next)
        {
            if (isCoalescable(node.Value))
            {
                sub.Queue.Remove(node);
                return true;
            }
        }

        // nothing droppable: drop the incoming item if it is coalescable, otherwise disconnect the subscriber
        if (!incomingCoalescable)
        {
            sub.Queue.Clear();
            sub.Error = new BroadcastOverflowException();
            sub.Signal.Release();
        }
        return false;
    }

    /// <summary>
    /// Ends every subscription after its queued items have been read. Later subscriptions get only the snapshot.
    /// </summary>
    public void Complete()
    {
        Subscriber[] subs;
        lock (this.sync)
        {
            if (this.completed)
                return;

            this.completed = true;
            subs = this.subscribers.ToArray();
        }

        foreach (var sub in subs)
        {
            lock (sub.Sync)
                sub.Completed = true;

            sub.Signal.Release();
        }
    }

    sealed class Subscriber(int capacity)
    {
        public readonly Lock Sync = new();
        public readonly LinkedList<T> Queue = new();
        public readonly SemaphoreSlim Signal = new(0);
        public readonly int Capacity = capacity;
        public bool Completed;
        public Exception? Error;
    }
}
