using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

/// <summary>
/// The connected mob changed tile. Sent at the moment of the move so the owning
/// client can start the glide in that tick, ahead of the replicated transform.
/// </summary>
public sealed class MsgMobStep : NetMessage {
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public float X;
    public float Y;
    public float GlideSize;

    /// <summary>
    /// Monotonic id of this committed step. A later snapshot of an earlier id is stale.
    /// </summary>
    public int Sequence;

    public override int EstimateBufferSize() => 16;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
        X = buffer.ReadFloat();
        Y = buffer.ReadFloat();
        GlideSize = buffer.ReadFloat();
        Sequence = buffer.ReadInt32();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
        buffer.Write(X);
        buffer.Write(Y);
        buffer.Write(GlideSize);
        buffer.Write(Sequence);
    }
}
