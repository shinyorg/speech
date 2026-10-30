using Shiny.AiConversation.Infrastructure;

namespace Shiny.AiConversation.Tests;

public class BroadcastChannelTests
{
    // negative numbers are "coalescable", everything else is lifecycle
    static BroadcastChannel<int> Create(int capacity = 256) => new(x => x < 0, capacity);


    static async Task<List<int>> ReadAll(IAsyncEnumerable<int> stream)
    {
        var list = new List<int>();
        await foreach (var i in stream)
            list.Add(i);
        return list;
    }


    static async Task WaitForSubscribers(BroadcastChannel<int> channel, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (channel.SubscriberCount < count)
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException();
            await Task.Delay(5);
        }
    }


    [Test]
    public async Task Snapshot_IsAlwaysFirst()
    {
        var channel = Create();
        var read = ReadAll(channel.Subscribe(() => 99));
        await WaitForSubscribers(channel, 1);

        channel.Publish(1);
        channel.Publish(2);
        channel.Complete();

        var items = await read;
        await Assert.That(String.Join(",", items)).IsEqualTo("99,1,2");
    }


    [Test]
    public async Task EverySubscriber_GetsEveryItem()
    {
        var channel = Create();
        var a = ReadAll(channel.Subscribe());
        var b = ReadAll(channel.Subscribe());
        await WaitForSubscribers(channel, 2);

        for (var i = 0; i < 50; i++)
            channel.Publish(i);
        channel.Complete();

        await Assert.That((await a).Count).IsEqualTo(50);
        await Assert.That((await b).Count).IsEqualTo(50);
    }


    [Test]
    public async Task SlowSubscriber_DropsOldestCoalescable_KeepsLifecycle()
    {
        var channel = Create(capacity: 3);
        var enumerator = channel.Subscribe().GetAsyncEnumerator();
        var first = enumerator.MoveNextAsync();     // registers the subscriber
        await WaitForSubscribers(channel, 1);
        channel.Publish(1);
        await first;                                 // reads 1

        channel.Publish(-1);
        channel.Publish(2);
        channel.Publish(-2);
        channel.Publish(3);                          // full: -1 dropped
        channel.Publish(-3);                         // full: -2 dropped
        channel.Complete();

        var rest = new List<int>();
        while (await enumerator.MoveNextAsync())
            rest.Add(enumerator.Current);

        await Assert.That(String.Join(",", rest)).IsEqualTo("2,3,-3");
    }


    [Test]
    public async Task SlowSubscriber_WithNothingToDrop_IsDisconnectedWithError()
    {
        var channel = Create(capacity: 2);
        var enumerator = channel.Subscribe().GetAsyncEnumerator();
        var first = enumerator.MoveNextAsync();
        await WaitForSubscribers(channel, 1);
        channel.Publish(0);
        await first;

        channel.Publish(1);
        channel.Publish(2);
        channel.Publish(3);   // lifecycle can't be dropped

        await Assert.That(async () => await enumerator.MoveNextAsync()).Throws<BroadcastOverflowException>();

        // the publisher and other subscribers are unaffected
        var other = ReadAll(channel.Subscribe());
        await WaitForSubscribers(channel, 1);
        channel.Publish(4);
        channel.Complete();
        await Assert.That(String.Join(",", await other)).IsEqualTo("4");
    }


    [Test]
    public async Task Cancellation_EndsSubscriptionNormally()
    {
        var channel = Create();
        using var cts = new CancellationTokenSource();
        var read = ReadAll(channel.Subscribe(ct: cts.Token));
        await WaitForSubscribers(channel, 1);

        channel.Publish(1);
        await Task.Delay(20);
        cts.Cancel();

        var items = await read;
        await Assert.That(items.Count).IsEqualTo(1);
        await WaitForSubscribers(channel, 0);
        await Assert.That(channel.SubscriberCount).IsEqualTo(0);
    }


    [Test]
    public async Task ConcurrentSubscribeAndUnsubscribe_DoesNotThrow()
    {
        var channel = Create();
        using var stop = new CancellationTokenSource();

        // only coalescable items: an unthrottled lifecycle flood would (correctly) disconnect a starved subscriber
        var publisher = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                channel.Publish(-(++i));
                await Task.Yield();
            }
        });

        var subscribers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 60)));
            var count = 0;
            await foreach (var _ in channel.Subscribe(() => 0, cts.Token))
                count++;
            return count;
        })).ToArray();

        var counts = await Task.WhenAll(subscribers);
        stop.Cancel();
        await publisher;
        channel.Complete();

        await Assert.That(counts.All(x => x >= 1)).IsTrue();   // at least the snapshot
        await Assert.That(channel.SubscriberCount).IsEqualTo(0);
    }


    [Test]
    public async Task SubscribeAfterComplete_GetsSnapshotThenEnds()
    {
        var channel = Create();
        channel.Complete();
        var items = await ReadAll(channel.Subscribe(() => 7));
        await Assert.That(String.Join(",", items)).IsEqualTo("7");
    }
}
