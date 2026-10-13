using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

/// <summary>
/// The connected mob's dir changed. Sent at the moment of the turn so the owning
/// client can face that way before the replicated appearance arrives.
/// </summary>
public sealed class MsgMobFacing : NetMessage {
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public byte Dir;

    public override int EstimateBufferSize() => 1;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        Dir = buffer.ReadByte();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.Write(Dir);
    }
}
