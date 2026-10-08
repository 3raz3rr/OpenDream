namespace OpenDreamRuntime.Objects.Types;

public sealed class DreamObjectSound : DreamObject {
    public ushort Channel, Volume;
    public float Offset;
    public byte Repeat { get; set => field = Math.Clamp(value, (byte)0, (byte)2); }
    public DreamValue File;
    public bool PositionSet;
    public int PosX, PosY, PosZ;
    public float Falloff = 1;

    public DreamObjectSound(DreamObjectDefinition objectDefinition) : base(objectDefinition) {
        if (objectDefinition.TryGetVariable("channel", out var channel)) {
            channel.TryGetValueAsInteger(out var channelValue);
            Channel = (ushort)channelValue;
        }

        if (objectDefinition.TryGetVariable("volume", out var volume)) {
            volume.TryGetValueAsInteger(out var volumeValue);
            Volume = (ushort)volumeValue;
        }

        if (objectDefinition.TryGetVariable("offset", out var offset)) {
            offset.TryGetValueAsFloat(out Offset);
        }

        if (objectDefinition.TryGetVariable("repeat", out var repeat)) {
            Repeat = (byte)repeat.UnsafeGetValueAsFloat();
        }

        objectDefinition.TryGetVariable("file", out File);

        if (objectDefinition.TryGetVariable("x", out var posX) && posX.TryGetValueAsInteger(out var xValue) && xValue != 0) {
            PosX = xValue;
            PositionSet = true;
        }

        if (objectDefinition.TryGetVariable("y", out var posY) && posY.TryGetValueAsInteger(out var yValue) && yValue != 0) {
            PosY = yValue;
            PositionSet = true;
        }

        if (objectDefinition.TryGetVariable("z", out var posZ) && posZ.TryGetValueAsInteger(out var zValue) && zValue != 0) {
            PosZ = zValue;
            PositionSet = true;
        }

        if (objectDefinition.TryGetVariable("falloff", out var falloff) && falloff.TryGetValueAsFloat(out var falloffValue))
            Falloff = falloffValue;
    }

    protected override bool TryGetVar(string varName, out DreamValue value) {
        switch (varName) {
            case "channel": value = new(Channel); return true;
            case "volume": value = new(Volume); return true;
            case "offset": value = new(Offset); return true;
            case "repeat": value = new(Repeat); return true;
            case "file": File.IncRef(); value = File; return true;
            case "x": value = new(PosX); return true;
            case "y": value = new(PosY); return true;
            case "z": value = new(PosZ); return true;
            case "falloff": value = new(Falloff); return true;
            default: return base.TryGetVar(varName, out value);
        }
    }

    protected override void SetVar(string varName, DreamValue value) {
        switch (varName) {
            case "channel": Channel = (ushort)value.UnsafeGetValueAsFloat(); break;
            case "volume": Volume = (ushort)value.UnsafeGetValueAsFloat(); break;
            case "offset": Offset = value.UnsafeGetValueAsFloat(); break;
            case "repeat": Repeat = (byte)value.UnsafeGetValueAsFloat(); break;
            case "file":
                value.IncRef();
                File.DecRef();
                File = value;
                break;
            case "x":
                PosX = value.MustGetValueAsInteger();
                PositionSet = true;
                break;
            case "y":
                PosY = value.MustGetValueAsInteger();
                PositionSet = true;
                break;
            case "z":
                PosZ = value.MustGetValueAsInteger();
                PositionSet = true;
                break;
            case "falloff":
                Falloff = value.UnsafeGetValueAsFloat();
                break;
            default: base.SetVar(varName, value); break;
        }
    }
}
