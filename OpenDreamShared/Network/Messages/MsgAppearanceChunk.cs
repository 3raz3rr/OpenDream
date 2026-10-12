using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

/// <summary>
/// One slice of the appearance dictionary for a client that just joined.
/// The client merges these until <see cref="MsgAppearancesComplete"/>.
/// </summary>
public sealed class MsgAppearanceChunk : NetMessage {
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public byte[] Data = [];

    public MsgAppearanceChunk() { }

    public MsgAppearanceChunk(byte[] data) {
        Data = data;
    }

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        var length = buffer.ReadVariableInt32();
        Data = buffer.ReadBytes(length);
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.WriteVariableInt32(Data.Length);
        buffer.Write(Data);
    }
}
