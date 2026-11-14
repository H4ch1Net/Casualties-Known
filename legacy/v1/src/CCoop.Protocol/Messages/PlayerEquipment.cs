using System;
using System.Collections.Generic;

namespace CCoop.Protocol.Messages
{
    /// <summary>One item a player has in a slot.</summary>
    public struct EquippedItem
    {
        /// <summary>Index into <c>Body.slots</c>, shared by every peer.</summary>
        public byte Slot;

        /// <summary>The item's id, as <c>Item.GetItem</c> consumes it.</summary>
        public string ItemId;
    }

    /// <summary>
    /// Owner -> host -> other clients. What a player is holding and wearing.
    ///
    /// Proxies are built by cloning the local body and then destroying every <c>Item</c>
    /// in the clone — which is necessary, because a live item on a proxy sits in
    /// <c>Item.allItems</c> and can be picked up out of a remote player's hands. The
    /// consequence was that remote players appeared empty-handed no matter what they
    /// carried, and there was no way to see a partner draw a weapon or hold a lamp.
    ///
    /// Only the slot and the id travel. Everything about how an item sits in a hand —
    /// rotation, scale, sort order, the extra quarter-turn some items take in a back slot
    /// — is derived from <c>ItemInfo</c> and the slot itself, both of which every peer
    /// already has. Sending any of it would be sending a value the receiver can look up.
    ///
    /// Empty slots are omitted rather than sent as blanks: a receiver clears any slot this
    /// message does not mention, so the common case of a player carrying one or two things
    /// costs a couple of dozen bytes.
    /// </summary>
    public sealed class PlayerEquipmentMessage
    {
        /// <summary>
        /// Ceiling on slots. The build has a handful; this only has to stop a hostile
        /// count sizing an allocation.
        /// </summary>
        public const int MaxItems = 32;

        public readonly List<EquippedItem> Items = new List<EquippedItem>();

        public byte[] Encode(byte senderPeerId, uint sessionId, uint sequence, uint tick)
        {
            if (Items.Count > MaxItems)
                throw new ArgumentException(
                    "equipment of " + Items.Count + " items exceeds " + MaxItems);

            var w = new PacketWriter(64 + Items.Count * 24);
            int lp = PacketHeader.Write(w, MessageType.PlayerEquipment, senderPeerId,
                                        sessionId, sequence, tick);
            w.WriteByte((byte)Items.Count);
            for (int i = 0; i < Items.Count; i++)
            {
                w.WriteByte(Items[i].Slot);
                w.WriteString(Items[i].ItemId ?? string.Empty);
            }
            PacketHeader.PatchPayloadLength(w, lp);
            return w.ToArray();
        }

        public static bool TryDecode(ref PacketReader r, out PlayerEquipmentMessage msg)
        {
            msg = null;

            int count = r.ReadByte();
            if (r.Failed) return false;
            if (count > MaxItems) return false;

            var m = new PlayerEquipmentMessage();
            for (int i = 0; i < count; i++)
            {
                byte slot = r.ReadByte();
                string id = r.ReadString();
                if (r.Failed) return false;

                // A named slot with no item is what an *omission* already means, so a
                // blank here is a malformed message rather than an empty hand.
                if (id.Length == 0) return false;

                m.Items.Add(new EquippedItem { Slot = slot, ItemId = id });
            }

            if (!r.FullyConsumed) return false;

            msg = m;
            return true;
        }
    }
}
