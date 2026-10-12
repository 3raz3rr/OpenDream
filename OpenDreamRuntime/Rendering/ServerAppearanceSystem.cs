using OpenDreamShared.Dream;
using Robust.Server.Player;
using Robust.Shared.Enums;
using System.Diagnostics.CodeAnalysis;
using OpenDreamShared.Network.Messages;
using Robust.Shared.Network;
using Robust.Shared.Player;
using System.Diagnostics;
using System.Threading;
using OpenDreamRuntime.Objects.Types;
using Robust.Shared.Map;
using Robust.Shared.Serialization;
using SharedAppearanceSystem = OpenDreamShared.Rendering.SharedAppearanceSystem;

namespace OpenDreamRuntime.Rendering;

public sealed partial class ServerAppearanceSystem : SharedAppearanceSystem {
    public ImmutableAppearance DefaultAppearance = default!;

    /// <summary>
    /// Each appearance gets a unique ID when marked as registered. Here we store these as a key -> weakref in a weaktable, which does not count
    /// as a hard ref but allows quick lookup. Each object which holds an appearance MUST hold that ImmutableAppearance until it is no longer
    /// needed or it will be GC'd. Overlays & underlays are stored as hard refs on the ImmutableAppearance so you only need to hold the main appearance.
    /// </summary>
    private readonly HashSet<ProxyWeakRef> _appearanceLookup = new();

    /// <summary>
    /// This system is used by the PVS thread, we need to be thread-safe
    /// </summary>
    private readonly Lock _lock = new();

    private readonly Queue<uint> _appearanceRemovalQueue = new();
    private readonly List<ImmutableAppearance> _appearanceSendQueue = new();
    private readonly Dictionary<uint, ProxyWeakRef> _idToAppearance = new();
    private readonly List<JoiningAppearances> _joining = new();
    private uint _counter;

    [Dependency] private DreamManager _dreamManager = default!;
    [Dependency] private AtomManager _atomManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IRobustSerializer _serializer = default!;

    public override void Initialize() {
        UpdatesOutsidePrediction = true;

        DefaultAppearance = new ImmutableAppearance(MutableAppearance.Default, this);
        DefaultAppearance.MarkRegistered(_counter++); //first appearance registered gets id 0, this is the blank default appearance
        ProxyWeakRef proxyWeakRef = new(DefaultAppearance);
        _appearanceLookup.Add(proxyWeakRef);
        _idToAppearance.Add(DefaultAppearance.MustGetId(), proxyWeakRef);
        //leaving this in as a sanity check for mutable and immutable appearance hashcodes covering all the same vars
        //if this debug assert fails, you've probably changed appearance var and not updated its counterpart
        Debug.Assert(DefaultAppearance.GetHashCode() == MutableAppearance.Default.GetHashCode());

        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown() {
        lock (_lock) {
            _appearanceLookup.Clear();
            _idToAppearance.Clear();
        }
    }

    public override void Update(float frameTime) {
        lock (_lock) {
            if (_appearanceSendQueue.Count > 0) {
                using var compressed = MsgAllAppearances.CompressAppearances(_appearanceSendQueue, _appearanceSendQueue.Count, _serializer);

                RaiseNetworkEvent(new NewAppearancesEvent(compressed.ToArray()));
                _appearanceSendQueue.Clear();
            }

            SendJoinBatches();

            if (_appearanceRemovalQueue.Count > 0) {
                var removalEvent = new RemoveAppearancesEvent(_appearanceRemovalQueue.ToArray());
                RaiseNetworkEvent(removalEvent);

                while (_appearanceRemovalQueue.TryDequeue(out var appearanceId)) {
                    var proxyWeakRef = _idToAppearance[appearanceId];

                    proxyWeakRef.TryGetTarget(out var appearance);
                    if (_appearanceLookup.TryGetValue(proxyWeakRef, out var weakRef)) {
                        //it is possible that a new appearance was created with the same hash before the GC got around to cleaning up the old one
                        if (weakRef.TryGetTarget(out var target) && !ReferenceEquals(target, appearance))
                            continue;

                        _appearanceLookup.Remove(proxyWeakRef);
                        _idToAppearance.Remove(appearanceId);
                    }
                }
            }
        }

        base.Update(frameTime);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e) {
        if (e.NewStatus == SessionStatus.Disconnected) {
            lock (_lock)
                _joining.RemoveAll(join => join.Channel == e.Session.Channel);
            return;
        }

        if (e.NewStatus != SessionStatus.InGame)
            return;

        // Compressing the whole dictionary here stalls the tick (join time dilation).
        // Snapshot the ids and send MsgAllAppearances.JoinBatchCount per Update instead.
        lock (_lock) {
            _joining.RemoveAll(join => join.Channel == e.Session.Channel);

            var ids = new uint[_idToAppearance.Count];
            var count = 0;
            foreach (var (id, weakRef) in _idToAppearance) {
                if (weakRef.TryGetTarget(out _))
                    ids[count++] = id;
            }

            if (count != ids.Length)
                Array.Resize(ref ids, count);

            Logger.GetSawmill("appearance").Info($"Queuing {count} appearances for {e.Session.Name} in batches of {MsgAllAppearances.JoinBatchCount}");
            _joining.Add(new JoiningAppearances(e.Session.Channel, ids));
        }
    }

    private void SendJoinBatches() {
        for (var i = _joining.Count - 1; i >= 0; i--) {
            var join = _joining[i];
            var end = Math.Min(join.Ids.Length, join.Cursor + MsgAllAppearances.JoinBatchCount);
            var batch = new List<ImmutableAppearance>(end - join.Cursor);
            for (; join.Cursor < end; join.Cursor++) {
                var id = join.Ids[join.Cursor];
                if (_idToAppearance.TryGetValue(id, out var weakRef) && weakRef.TryGetTarget(out var appearance))
                    batch.Add(appearance);
            }

            if (batch.Count > 0) {
                using var compressed = MsgAllAppearances.CompressAppearances(batch, batch.Count, _serializer);
                join.Channel.SendMessage(new MsgAppearanceChunk(compressed.ToArray()));
            }

            if (join.Cursor < join.Ids.Length)
                continue;

            join.Channel.SendMessage(new MsgAppearancesComplete());
            Logger.GetSawmill("appearance").Info($"Finished appearance sync for {join.Ids.Length} appearances");
            _joining.RemoveAt(i);
        }
    }

    private void RegisterAppearance(ImmutableAppearance immutableAppearance) {
        immutableAppearance.MarkRegistered(_counter++); //lets this appearance know it needs to do GC finaliser & get an ID

        ProxyWeakRef proxyWeakRef = new(immutableAppearance);
        _appearanceLookup.Add(proxyWeakRef);
        _idToAppearance.Add(immutableAppearance.MustGetId(), proxyWeakRef);
        _appearanceSendQueue.Add(immutableAppearance);
    }

    public ImmutableAppearance AddAppearance(MutableAppearance appearance, bool registerAppearance = true) {
        ImmutableAppearance immutableAppearance = new(appearance, this);

        return AddAppearance(immutableAppearance, registerAppearance);
    }

    public ImmutableAppearance AddAppearance(ImmutableAppearance appearance, bool registerAppearance = true) {
        lock (_lock) {
            if(_appearanceLookup.TryGetValue(new(appearance), out var weakReference) && weakReference.TryGetTarget(out var originalImmutable)) {
                return originalImmutable;
            } else if (registerAppearance) {
                RegisterAppearance(appearance);
                return appearance;
            } else {
                return appearance;
            }
        }
    }

    //this should only be called by the ImmutableAppearance's finalizer
    [Access(typeof(ImmutableAppearance))]
    public override void RemoveAppearance(ImmutableAppearance appearance) {
        lock (_lock) {
            _appearanceRemovalQueue.Enqueue(appearance.MustGetId());
        }
    }

    public override ImmutableAppearance MustGetAppearanceById(uint appearanceId) {
        lock (_lock) {
            if(!_idToAppearance[appearanceId].TryGetTarget(out var result))
                throw new Exception($"Attempted to access deleted appearance ID ${appearanceId} in MustGetAppearanceByID()");
            return result;
        }
    }

    public bool TryGetAppearanceById(uint appearanceId, [NotNullWhen(true)] out ImmutableAppearance? appearance) {
        lock (_lock) {
            appearance = null;
            return _idToAppearance.TryGetValue(appearanceId, out var appearanceRef) && appearanceRef.TryGetTarget(out appearance);
        }
    }

    public void Animate(EntityUid entity, MutableAppearance targetAppearance, TimeSpan duration, AnimationEasing easing, int loop, AnimationFlags flags, int delay, bool chainAnim, uint? turfId) {
        var appearanceId = AddAppearance(targetAppearance).MustGetId();
        var netEntity = GetNetEntity(entity);
        var animateEvent = new AnimationEvent(netEntity, appearanceId, duration, easing, loop, flags, delay, chainAnim, turfId);

        if (entity.IsValid())
            RaiseNetworkEvent(animateEvent, Filter.Pvs(entity));
        else
            RaiseNetworkEvent(animateEvent); // TODO: Filter for non-entities
    }

    public void Flick(DreamObjectAtom atom, int iconId, string? iconState) {
        var position = _atomManager.GetAtomPosition(atom);
        var mapCoords = new MapCoordinates(position.X, position.Y, new(position.Z));
        var clientRef = _dreamManager.GetClientReference(atom);
        var flickEvent = new FlickEvent(clientRef, iconId, iconState);

        RaiseNetworkEvent(flickEvent, Filter.Pvs(mapCoords));
    }
}

internal sealed class JoiningAppearances(INetChannel channel, uint[] ids) {
    public readonly INetChannel Channel = channel;
    public readonly uint[] Ids = ids;
    public int Cursor;
}

//this class lets us hold a weakref and also do quick lookups in hash tables
internal sealed class ProxyWeakRef: IEquatable<ProxyWeakRef> {
    private readonly uint? _registeredId;
    private readonly int _hashCode;
    public bool TryGetTarget([NotNullWhen(true)] out ImmutableAppearance? target) => _weakRef.TryGetTarget(out target);

    private readonly WeakReference<ImmutableAppearance> _weakRef;

    public ProxyWeakRef(ImmutableAppearance appearance) {
        appearance.TryGetId(out _registeredId);
        _weakRef = new(appearance);
        _hashCode = appearance.GetHashCode();
    }

    public override int GetHashCode() {
        return _hashCode;
    }

    public override bool Equals(object? obj) => obj is ProxyWeakRef proxy && Equals(proxy);

    public bool Equals(ProxyWeakRef? proxy) {
        if(proxy is null)
            return false;
        if(_registeredId is not null && _registeredId == proxy._registeredId)
            return true;
        if(_weakRef.TryGetTarget(out ImmutableAppearance? thisRef) && proxy._weakRef.TryGetTarget(out ImmutableAppearance? thatRef))
            return thisRef.Equals(thatRef);
        return false;
    }
}
