using System.Diagnostics;
using System.IO;
using System.Reflection;
using OpenDreamShared.Dream;
using OpenDreamShared.Network.Messages;
using Robust.Shared.Network;
using Robust.Shared.Serialization;
using SpaceWizards.Sodium;

// Headless numbers for the two costs that hitch a join:
// the full appearance dictionary (MsgAllAppearances) and hashing a browse_rsc payload.
// This does not open a window, CEF, or the station.

const int warmup = 1500;
int[] counts = args.Length > 0 ? [int.Parse(args[0])] : [8_000, 40_000];

var serializer = new UnusedSerializer();

var lines = new List<string> {
    "OpenDream load bench",
    "Full dump is the old join path. Join batch is one server tick of the split send.",
    "Draw and CEF are not in this run.",
    "",
    $"{"what",-22} {"count",8} {"wire",10} {"pack",8} {"unpack",8} {"apply",8} {"alloc",8}"
};

RunAppearances(warmup, print: false);
foreach (var count in counts)
    RunAppearances(count, print: true);

var changed = Math.Max(1, counts[^1] / 50);
RunAppearances(changed, print: true, label: $"changed ~2% of {counts[^1]}");
RunJoinBatches(counts[^1]);

CryptoGenericHashBlake2B.Hash(32, new byte[64], ReadOnlySpan<byte>.Empty);
GC.Collect();

lines.Add("");
lines.Add("browse_rsc hash (same Blake2 the server runs on the whole file before send)");
HashBundle("tgui.bundle.js", 4_550_000);
HashBundle("tgui-panel", 488_000);
HashBundle("tgui-statpanel", 336_000);
HashBundle("orbitalis-shell", 627_000);

foreach (var line in lines)
    Console.WriteLine(line);

var reportPath = Path.Combine(AppContext.BaseDirectory, "last.txt");
File.WriteAllLines(reportPath, lines);
Console.WriteLine();
Console.WriteLine(reportPath);

void RunAppearances(int count, bool print, string? label = null) {
    var appearances = MakeAppearances(count);
    var packed = Time(() => MsgAllAppearances.CompressAppearances(appearances, appearances.Length, serializer));
    var wire = (int)packed.Value.Position;

    var payload = packed.Value.GetBuffer();
    var unpacked = Time(() => {
        var stream = new MemoryStream(payload, 0, wire, writable: false);
        var buffer = MsgAllAppearances.DecompressAppearances(stream);
        var n = buffer.ReadInt32();
        var read = new ImmutableAppearance[n];
        for (var i = 0; i < n; i++)
            read[i] = new ImmutableAppearance(buffer, serializer);
        return read;
    });

    if (unpacked.Value.Length != count)
        throw new InvalidOperationException($"round-trip count {unpacked.Value.Length} != {count}");

    var applied = Time(() => BucketByPlane(unpacked.Value));
    var allocMb = unpacked.Allocated / (1024d * 1024d);

    if (print) {
        lines.Add(
            $"{label ?? "appearance dump",-22} {count,8} {FormatBytes(wire),10} {packed.Ms,7:0.0}ms {unpacked.Ms,7:0.0}ms {applied.Ms,7:0.0}ms {allocMb,6:0.0}MB");
    }
}

void RunJoinBatches(int count) {
    var appearances = MakeAppearances(count);
    var batch = MsgAllAppearances.JoinBatchCount;
    double maxPack = 0, maxUnpack = 0, packSum = 0, unpackSum = 0;
    long allocSum = 0, maxAlloc = 0;
    var wire = 0;
    var maxWire = 0;
    var batches = 0;

    for (var offset = 0; offset < appearances.Length; offset += batch) {
        var len = Math.Min(batch, appearances.Length - offset);
        var slice = new ArraySegment<ImmutableAppearance>(appearances, offset, len);
        var packed = Time(() => MsgAllAppearances.CompressAppearances(slice, len, serializer));
        var batchWire = (int)packed.Value.Position;
        var payload = packed.Value.GetBuffer();
        var unpacked = Time(() => {
            var stream = new MemoryStream(payload, 0, batchWire, writable: false);
            var buffer = MsgAllAppearances.DecompressAppearances(stream);
            var n = buffer.ReadInt32();
            var read = new ImmutableAppearance[n];
            for (var i = 0; i < n; i++)
                read[i] = new ImmutableAppearance(buffer, serializer);
            return read;
        });

        if (unpacked.Value.Length != len)
            throw new InvalidOperationException($"batch count {unpacked.Value.Length} != {len}");

        batches++;
        wire += batchWire;
        packSum += packed.Ms;
        unpackSum += unpacked.Ms;
        allocSum += unpacked.Allocated;
        maxPack = Math.Max(maxPack, packed.Ms);
        maxUnpack = Math.Max(maxUnpack, unpacked.Ms);
        maxWire = Math.Max(maxWire, batchWire);
        maxAlloc = Math.Max(maxAlloc, unpacked.Allocated);
    }

    lines.Add(
        $"{"one join batch",-22} {batch,8} {FormatBytes(maxWire),10} {maxPack,7:0.0}ms {maxUnpack,7:0.0}ms {"",8} {maxAlloc / (1024d * 1024d),6:0.0}MB");
    lines.Add(
        $"{"all join batches",-22} {batches,8} {FormatBytes(wire),10} {packSum,7:0.0}ms {unpackSum,7:0.0}ms {"",8} {allocSum / (1024d * 1024d),6:0.0}MB");
}

static ImmutableAppearance[] MakeAppearances(int count) {
    var list = new ImmutableAppearance[count];
    var idField = typeof(ImmutableAppearance).GetField("_registeredId", BindingFlags.Instance | BindingFlags.NonPublic)!;
    for (var i = 0; i < count; i++) {
        var mutable = MutableAppearance.Get();
        mutable.Icon = i % 400;
        mutable.IconState = (i % 7) switch {
            0 => "floor",
            1 => "wall",
            2 => "engine",
            3 => null,
            _ => "state" + (i % 40)
        };
        mutable.Plane = (i % 11) switch {
            0 => -6,
            1 => -5,
            2 => -4,
            3 => 2,
            _ => -4
        };
        mutable.Layer = (i % 20) / 10f;
        mutable.Invisibility = (sbyte)(i % 17 == 0 ? 20 : 0);
        mutable.GlideSize = i % 23 == 0 ? 32 : 0;
        mutable.Alpha = (byte)(i % 29 == 0 ? 180 : 255);
        if (i % 5 == 0 && i > 0)
            mutable.Overlays.Add(list[i - 1]);

        var appearance = new ImmutableAppearance(mutable, serverAppearanceSystem: null);
        // Skip MarkRegistered: it arms a finalizer that needs the live appearance system.
        idField.SetValue(appearance, (uint)(i + 1));
        mutable.Dispose();
        list[i] = appearance;
    }

    return list;
}

static int BucketByPlane(ImmutableAppearance[] appearances) {
    Span<int> buckets = stackalloc int[8];
    foreach (var appearance in appearances) {
        var slot = appearance.Plane switch {
            -6 => 0,
            -5 => 1,
            -4 => 2,
            2 => 3,
            _ => 4
        };
        buckets[slot]++;
        _ = appearance.Invisibility > 0 && appearance.Plane == 2;
    }

    return buckets[0] + buckets[1] + buckets[2] + buckets[3] + buckets[4];
}

void HashBundle(string name, int bytes) {
    var data = new byte[bytes];
    for (var i = 0; i < data.Length; i += 64)
        data[i] = (byte)i;

    var timed = Time(() => CryptoGenericHashBlake2B.Hash(32, data, ReadOnlySpan<byte>.Empty));
    if (timed.Value.Length != 32)
        throw new InvalidOperationException("hash length");
    lines.Add($"  {name,-18} {FormatBytes(bytes),10}  {timed.Ms,7:0.0}ms");
}

static Timed<T> Time<T>(Func<T> work) {
    var before = GC.GetAllocatedBytesForCurrentThread();
    var clock = Stopwatch.StartNew();
    var value = work();
    clock.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    return new Timed<T>(value, clock.Elapsed.TotalMilliseconds, allocated);
}

static string FormatBytes(int bytes) {
    if (bytes >= 1024 * 1024)
        return $"{bytes / (1024d * 1024d):0.00}MB";
    return $"{bytes / 1024d:0}KB";
}

readonly record struct Timed<T>(T Value, double Ms, long Allocated);

sealed class UnusedSerializer : IRobustSerializer {
    public SerializerFloatFlags FloatFlags { get => SerializerFloatFlags.None; set { } }
    public void Initialize() { }
    public void Serialize(Stream stream, object toSerialize) => throw new NotSupportedException();
    public void SerializeDirect<T>(Stream stream, T toSerialize) => throw new NotSupportedException();
    public T Deserialize<T>(Stream stream) => throw new NotSupportedException();
    public void DeserializeDirect<T>(Stream stream, out T value) => throw new NotSupportedException();
    public object Deserialize(Stream stream) => throw new NotSupportedException();
    public bool CanSerialize(Type type) => false;
    public Type? FindSerializedType(Type assignableType, string serializedTypeName) => null;
    public Task Handshake(INetChannel sender) => Task.CompletedTask;
    public event Action? ClientHandshakeComplete { add { } remove { } }
    public byte[] GetSerializableTypesHash() => [];
    public string GetSerializableTypesHashString() => "";
    public (byte[] Hash, byte[] Package) GetStringSerializerPackage() => ([], []);
}
