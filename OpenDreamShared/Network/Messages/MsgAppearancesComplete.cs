using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace OpenDreamShared.Network.Messages;

/// <summary>
/// The joining client has every appearance from the moment it entered the game.
/// Overlay ids can be resolved after this.
/// </summary>
public sealed class MsgAppearancesComplete : NetMessage {
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer) {
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer) {
    }
}
