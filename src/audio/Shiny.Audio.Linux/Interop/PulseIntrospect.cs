using System.Runtime.InteropServices;

namespace Shiny.Audio.Interop;

/// <summary>A sink (output) or source (input) as reported by the PulseAudio server.</summary>
/// <param name="VolumeChannels">
/// Channel count of the device's <c>pa_cvolume</c>. A volume set must carry the same count the sink
/// reports, so it has to be read back rather than assumed.
/// </param>
record PulseDevice(uint Index, string Name, string Description, bool IsInput, float Volume, byte VolumeChannels);

/// <summary>
/// Bindings for the PulseAudio introspection API — everything <c>pa_simple</c> cannot do: device
/// enumeration, the default sink/source, volume get/set, and route-change notifications.
/// </summary>
/// <remarks>
/// <para>
/// Queries run on a short-lived <c>pa_mainloop</c> that is created, driven to completion and torn
/// down inside a single call (see <see cref="PulseSession"/>). That costs a connect per query, but
/// it sidesteps the entire thread-safety problem: a mainloop is only ever touched by the thread
/// that created it. Change notifications get their own long-lived session on a dedicated thread,
/// which likewise never shares its mainloop.
/// </para>
/// <para>
/// Struct offsets are computed from <see cref="IntPtr.Size"/> rather than hard-coded, so the same
/// code is correct on 64-bit desktops and on 32-bit ARM (Raspberry Pi OS armhf). Only the leading
/// fields of <c>pa_sink_info</c>/<c>pa_source_info</c> are read — the trailing <c>proplist</c> and
/// port arrays are never dereferenced, so a layout drift further down the struct cannot hurt us.
/// </para>
/// </remarks>
static partial class PulseIntrospect
{
    const string Lib = "libpulse.so.0";

    const int StateReady = 4;
    const int StateFailed = 5;
    const int StateTerminated = 6;

    const int OperationRunning = 0;

    /// <summary>PA_VOLUME_NORM — 100%, the 0 dB reference.</summary>
    const uint VolumeNorm = 0x10000;

    /// <summary>PA_SUBSCRIPTION_MASK_SINK | _SOURCE | _SERVER.</summary>
    const uint SubscriptionMask = 0x0001 | 0x0002 | 0x0100;

    [LibraryImport(Lib, EntryPoint = "pa_mainloop_new")]
    internal static partial IntPtr MainloopNew();

    [LibraryImport(Lib, EntryPoint = "pa_mainloop_free")]
    internal static partial void MainloopFree(IntPtr mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_mainloop_get_api")]
    internal static partial IntPtr MainloopGetApi(IntPtr mainloop);

    [LibraryImport(Lib, EntryPoint = "pa_mainloop_iterate")]
    internal static partial int MainloopIterate(IntPtr mainloop, int block, out int retval);

    [LibraryImport(Lib, EntryPoint = "pa_context_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ContextNew(IntPtr mainloopApi, string name);

    [LibraryImport(Lib, EntryPoint = "pa_context_connect", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ContextConnect(IntPtr context, string? server, int flags, IntPtr spawnApi);

    [LibraryImport(Lib, EntryPoint = "pa_context_disconnect")]
    internal static partial void ContextDisconnect(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "pa_context_unref")]
    internal static partial void ContextUnref(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_state")]
    internal static partial int ContextGetState(IntPtr context);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_sink_info_list")]
    internal static partial IntPtr GetSinkInfoList(IntPtr context, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_source_info_list")]
    internal static partial IntPtr GetSourceInfoList(IntPtr context, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_context_get_server_info")]
    internal static partial IntPtr GetServerInfo(IntPtr context, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_sink_volume_by_name", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr SetSinkVolumeByName(IntPtr context, string name, ref byte volume, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_context_subscribe")]
    internal static partial IntPtr ContextSubscribe(IntPtr context, uint mask, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_context_set_subscribe_callback")]
    internal static partial void SetSubscribeCallback(IntPtr context, IntPtr callback, IntPtr userData);

    [LibraryImport(Lib, EntryPoint = "pa_operation_get_state")]
    internal static partial int OperationGetState(IntPtr operation);

    [LibraryImport(Lib, EntryPoint = "pa_operation_unref")]
    internal static partial void OperationUnref(IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InfoCallback(IntPtr context, IntPtr info, int eol, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ServerInfoCallback(IntPtr context, IntPtr info, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SubscribeCallback(IntPtr context, uint eventType, uint index, IntPtr userData);

    // Held in static fields so the GC can never collect the thunks while PulseAudio holds pointers
    // to them.
    static readonly InfoCallback infoCallback = OnInfo;
    static readonly ServerInfoCallback serverInfoCallback = OnServerInfo;
    static readonly SubscribeCallback subscribeCallback = OnSubscribe;

    static readonly IntPtr infoCallbackPtr = Marshal.GetFunctionPointerForDelegate(infoCallback);
    static readonly IntPtr serverInfoCallbackPtr = Marshal.GetFunctionPointerForDelegate(serverInfoCallback);
    static readonly IntPtr subscribeCallbackPtr = Marshal.GetFunctionPointerForDelegate(subscribeCallback);

    #region struct offsets

    // pa_sink_info / pa_source_info share this leading layout:
    //   const char *name; uint32_t index; const char *description;
    //   pa_sample_spec sample_spec;      (12 bytes)
    //   pa_channel_map channel_map;      (132 bytes: uint8 + pad + int[32])
    //   uint32_t owner_module;
    //   pa_cvolume volume;               (132 bytes: uint8 + pad + uint32[32])
    //   int mute;
    /// <summary>PA_CHANNELS_MAX.</summary>
    const int ChannelsMax = 32;

    const int SampleSpecSize = 12;
    const int ChannelMapSize = 4 + ChannelsMax * 4;    // uint8 channel count + padding, then int[32]
    const int CVolumeSize = 4 + ChannelsMax * 4;       // uint8 channel count + padding, then uint32[32]

    static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    static int OffsetIndex => IntPtr.Size;
    static int OffsetDescription => Align(IntPtr.Size + sizeof(uint), IntPtr.Size);
    static int OffsetSampleSpec => OffsetDescription + IntPtr.Size;
    static int OffsetOwnerModule => OffsetSampleSpec + SampleSpecSize + ChannelMapSize;
    static int OffsetVolume => OffsetOwnerModule + sizeof(uint);

    /// <summary>First per-channel value inside <c>pa_cvolume</c> (past the byte channel count + padding).</summary>
    static int OffsetVolumeFirstChannel => OffsetVolume + 4;

    // pa_server_info: four leading strings, then a pa_sample_spec, then the two default names.
    static int OffsetDefaultSinkName => Align(4 * IntPtr.Size + SampleSpecSize, IntPtr.Size);
    static int OffsetDefaultSourceName => OffsetDefaultSinkName + IntPtr.Size;

    #endregion

    /// <summary>
    /// Everything a query accumulates. Passed to the native callbacks through a
    /// <see cref="GCHandle"/> so the thunks stay static and closure-free.
    /// </summary>
    sealed class QueryState
    {
        public readonly List<PulseDevice> Devices = [];
        public bool IsInput;
        public bool Completed;
        public string? DefaultSinkName;
        public string? DefaultSourceName;
    }

    static void OnInfo(IntPtr context, IntPtr info, int eol, IntPtr userData)
    {
        // A native callback must never let an exception escape into the C stack.
        try
        {
            var state = (QueryState?)GCHandle.FromIntPtr(userData).Target;
            if (state == null)
                return;

            if (eol != 0 || info == IntPtr.Zero)
            {
                state.Completed = true;
                return;
            }

            var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info));
            if (name == null)
                return;

            var description = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, OffsetDescription)) ?? name;
            var index = (uint)Marshal.ReadInt32(info, OffsetIndex);
            var rawVolume = (uint)Marshal.ReadInt32(info, OffsetVolumeFirstChannel);
            var volumeChannels = Marshal.ReadByte(info, OffsetVolume);

            state.Devices.Add(new PulseDevice(
                index,
                name,
                description,
                state.IsInput,
                Math.Clamp((float)rawVolume / VolumeNorm, 0f, 1f),
                volumeChannels
            ));
        }
        catch
        {
            // swallow — there is no managed frame above us to catch it
        }
    }

    static void OnServerInfo(IntPtr context, IntPtr info, IntPtr userData)
    {
        try
        {
            var state = (QueryState?)GCHandle.FromIntPtr(userData).Target;
            if (state == null)
                return;

            if (info != IntPtr.Zero)
            {
                state.DefaultSinkName = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, OffsetDefaultSinkName));
                state.DefaultSourceName = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(info, OffsetDefaultSourceName));
            }
            state.Completed = true;
        }
        catch
        {
        }
    }

    static void OnSubscribe(IntPtr context, uint eventType, uint index, IntPtr userData)
    {
        try
        {
            (GCHandle.FromIntPtr(userData).Target as Action)?.Invoke();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Enumerate every sink and source, tagging the server's current defaults. Returns an empty
    /// list when no server is reachable.
    /// </summary>
    internal static IReadOnlyList<PulseDevice> GetDevices(out string? defaultSink, out string? defaultSource)
    {
        defaultSink = null;
        defaultSource = null;

        using var session = PulseSession.TryConnect();
        if (session == null)
            return [];

        var state = new QueryState();
        var handle = GCHandle.Alloc(state);
        try
        {
            var userData = GCHandle.ToIntPtr(handle);

            state.IsInput = false;
            session.RunToCompletion(GetSinkInfoList(session.Context, infoCallbackPtr, userData), () => state.Completed);

            state.Completed = false;
            state.IsInput = true;
            session.RunToCompletion(GetSourceInfoList(session.Context, infoCallbackPtr, userData), () => state.Completed);

            state.Completed = false;
            session.RunToCompletion(GetServerInfo(session.Context, serverInfoCallbackPtr, userData), () => state.Completed);

            defaultSink = state.DefaultSinkName;
            defaultSource = state.DefaultSourceName;
            return state.Devices;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Current volume of a sink (0–1), or null when it can't be read.</summary>
    internal static float? GetSinkVolume(string? sinkName)
    {
        var devices = GetDevices(out var defaultSink, out _);
        var target = sinkName ?? defaultSink;

        var sink = target == null
            ? devices.FirstOrDefault(x => !x.IsInput)
            : devices.FirstOrDefault(x => !x.IsInput && x.Name == target);

        return sink?.Volume;
    }

    /// <summary>Set a sink's volume (0–1) across all of its channels. Returns false when it can't be applied.</summary>
    internal static bool SetSinkVolume(string? sinkName, float volume)
    {
        var devices = GetDevices(out var defaultSink, out _);
        var target = sinkName ?? defaultSink;

        var sink = target == null
            ? devices.FirstOrDefault(x => !x.IsInput)
            : devices.FirstOrDefault(x => !x.IsInput && x.Name == target);

        if (sink == null)
            return false;

        using var session = PulseSession.TryConnect();
        if (session == null)
            return false;

        // pa_cvolume: a byte channel count (padded to 4) followed by PA_CHANNELS_MAX per-channel uint
        // values. Built by hand because a fixed-size array would make the struct non-blittable.
        //
        // The channel count must match what the sink reported — the server validates it, so writing a
        // blanket 32 here would be rejected on an ordinary stereo sink. The full value array is still
        // populated so the struct is well-defined regardless of count.
        var channels = sink.VolumeChannels == 0 ? (byte)2 : sink.VolumeChannels;
        var raw = (uint)Math.Round(Math.Clamp(volume, 0f, 1f) * VolumeNorm);

        Span<byte> cvolume = stackalloc byte[4 + ChannelsMax * sizeof(uint)];
        cvolume.Clear();
        cvolume[0] = channels;
        for (var i = 0; i < ChannelsMax; i++)
            MemoryMarshal.Write(cvolume.Slice(4 + i * sizeof(uint), sizeof(uint)), in raw);

        var operation = SetSinkVolumeByName(session.Context, sink.Name, ref cvolume[0], IntPtr.Zero, IntPtr.Zero);
        session.RunToCompletion(operation, () => false);
        return true;
    }

    /// <summary>
    /// Watch for sink/source/server changes on a dedicated thread, invoking <paramref name="onChanged"/>
    /// for each. Dispose the returned handle to stop watching.
    /// </summary>
    internal static IDisposable? Subscribe(Action onChanged) => PulseSubscription.TryStart(onChanged);

    /// <summary>
    /// Owns a <c>pa_mainloop</c> + connected <c>pa_context</c> pair. Never share one across threads:
    /// a mainloop must only be driven by the thread that created it.
    /// </summary>
    internal sealed class PulseSession : IDisposable
    {
        const int ConnectTimeoutMs = 2000;
        const int OperationTimeoutMs = 2000;

        IntPtr mainloop;
        internal IntPtr Context { get; private set; }

        /// <summary>The raw mainloop, for callers that pump it themselves (see the subscription thread).</summary>
        internal IntPtr MainloopHandle => this.mainloop;

        PulseSession(IntPtr mainloop, IntPtr context)
        {
            this.mainloop = mainloop;
            this.Context = context;
        }

        /// <summary>Connect to the local server, or null when there isn't one.</summary>
        internal static PulseSession? TryConnect()
        {
            IntPtr mainloop = IntPtr.Zero, context = IntPtr.Zero;
            try
            {
                mainloop = MainloopNew();
                if (mainloop == IntPtr.Zero)
                    return null;

                context = ContextNew(MainloopGetApi(mainloop), "Shiny.Audio");
                if (context == IntPtr.Zero)
                {
                    MainloopFree(mainloop);
                    return null;
                }

                if (ContextConnect(context, null, 0, IntPtr.Zero) < 0)
                {
                    Teardown(mainloop, context);
                    return null;
                }

                var deadline = Environment.TickCount64 + ConnectTimeoutMs;
                while (true)
                {
                    var state = ContextGetState(context);
                    if (state == StateReady)
                        return new PulseSession(mainloop, context);

                    if (state == StateFailed || state == StateTerminated || Environment.TickCount64 > deadline)
                    {
                        Teardown(mainloop, context);
                        return null;
                    }

                    Pump(mainloop);
                }
            }
            catch (DllNotFoundException)
            {
                Teardown(mainloop, context);
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                Teardown(mainloop, context);
                return null;
            }
        }

        /// <summary>
        /// Drive the mainloop until the operation reports done (or <paramref name="isComplete"/> says
        /// the callbacks are finished), then release it.
        /// </summary>
        internal void RunToCompletion(IntPtr operation, Func<bool> isComplete)
        {
            if (operation == IntPtr.Zero)
                return;

            var deadline = Environment.TickCount64 + OperationTimeoutMs;
            while (OperationGetState(operation) == OperationRunning && !isComplete() && Environment.TickCount64 < deadline)
                Pump(this.mainloop);

            OperationUnref(operation);
        }

        // Non-blocking iteration + a short sleep, rather than a blocking iterate: a blocked mainloop
        // can only be woken from its own thread, which would make the timeouts above unenforceable.
        static void Pump(IntPtr mainloop)
        {
            if (MainloopIterate(mainloop, 0, out _) < 0)
                return;

            Thread.Sleep(1);
        }

        static void Teardown(IntPtr mainloop, IntPtr context)
        {
            if (context != IntPtr.Zero)
            {
                ContextDisconnect(context);
                ContextUnref(context);
            }
            if (mainloop != IntPtr.Zero)
                MainloopFree(mainloop);
        }

        public void Dispose()
        {
            Teardown(this.mainloop, this.Context);
            this.mainloop = IntPtr.Zero;
            this.Context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// A long-lived subscription running its own session on a dedicated thread. The mainloop is
    /// created, pumped and destroyed entirely on that thread.
    /// </summary>
    sealed class PulseSubscription : IDisposable
    {
        volatile bool stopped;
        readonly Thread thread;
        readonly Action onChanged;
        GCHandle handle;

        PulseSubscription(Action onChanged)
        {
            this.onChanged = onChanged;
            this.thread = new Thread(this.Run) { IsBackground = true, Name = "Shiny.Audio PulseAudio events" };
        }

        internal static PulseSubscription? TryStart(Action onChanged)
        {
            var subscription = new PulseSubscription(onChanged);
            subscription.thread.Start();
            return subscription;
        }

        void Run()
        {
            using var session = PulseSession.TryConnect();
            if (session == null)
                return;

            this.handle = GCHandle.Alloc(this.onChanged);
            try
            {
                var userData = GCHandle.ToIntPtr(this.handle);
                SetSubscribeCallback(session.Context, subscribeCallbackPtr, userData);
                session.RunToCompletion(ContextSubscribe(session.Context, SubscriptionMask, IntPtr.Zero, IntPtr.Zero), () => false);

                // 50ms of latency on a route-change notification is imperceptible, and polling this
                // way keeps every mainloop touch on this one thread.
                while (!this.stopped)
                {
                    MainloopIterate(session.MainloopHandle, 0, out _);
                    Thread.Sleep(50);
                }
            }
            finally
            {
                if (this.handle.IsAllocated)
                    this.handle.Free();
            }
        }

        public void Dispose() => this.stopped = true;
    }
}
