using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;
using CCoop.Plugin.UI;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Dying costs your kit, not the run. While a player is dead, their body is lootable:
    /// teammates can take what's in their pockets, bags and clothes — never what's in their
    /// hands or mouth. The dead player's own machine hands each item over (the same
    /// hand-over as giving), so nothing duplicates. Whatever nobody took is still theirs
    /// when they come back.
    /// </summary>
    internal sealed class LootSync : CoopModule
    {
        private const int MouthSlot = 2;  // the game's "holding something in the mouth" slot

        private float _nextList;
        private bool _listedDead;
        private readonly Dictionary<int, Item> _offered = new Dictionary<int, Item>();
        private readonly Dictionary<byte, List<LootList.Entry>> _lists = new Dictionary<byte, List<LootList.Entry>>();

        internal override void Attach(NetSession s)
        {
            s.On<LootList>((o, m) => _lists[o] = m.Items);
            s.On<LootTake>(OnTake);
        }

        internal override void Detach()
        {
            _offered.Clear();
            _lists.Clear();
        }

        internal override void OnWorldLost() => _lists.Clear();

        internal override void OnPeerLeft(PeerInfo peer) => _lists.Remove(peer.Id);

        /// <summary>What can be taken from a dead teammate's body (empty if they're alive).</summary>
        internal List<LootList.Entry> Of(byte id) =>
            _lists.TryGetValue(id, out var l) && !Coop.Player(id).Alive ? l : null;

        // ------------------------------------------------------------------ owner (the dead)

        /// <summary>Pockets, bags and clothes: everything but the hands and the mouth.</summary>
        internal static List<Item> Lootable(Body body)
        {
            var list = new List<Item>();
            if (body == null) return list;
            if (body.slots != null)
                for (int i = 0; i < body.slots.Length; i++)
                {
                    var slot = body.slots[i];
                    if (slot == null || slot.isHand || i == MouthSlot) continue;
                    Item item = null;
                    try { item = body.GetItem(i); } catch { /* empty slot */ }
                    if (item != null && !list.Contains(item)) list.Add(item);
                }
            if (body.limbs != null)
                foreach (var limb in body.limbs)
                {
                    if (limb == null) continue;
                    foreach (Transform child in limb.transform)
                        if (child.TryGetComponent<Item>(out var worn) && worn.Stats.wearable && !list.Contains(worn)) list.Add(worn);
                }
            return list;
        }

        internal override void Tick()
        {
            var body = GameInfo.LocalBody;
            bool dead = Coop.WorldLive && body != null && !body.alive;
            if (!dead)
            {
                if (_listedDead) Session.Broadcast(new LootList());  // alive again: nothing to loot
                _listedDead = false;
                _offered.Clear();
                return;
            }
            if (Time.unscaledTime < _nextList) return;
            _nextList = Time.unscaledTime + 1f;
            _listedDead = true;
            _offered.Clear();
            var msg = new LootList();
            foreach (var item in Lootable(body))
            {
                int key = item.GetInstanceID();
                _offered[key] = item;
                msg.Items.Add(new LootList.Entry { Key = key, ItemId = item.id });
            }
            Session.Broadcast(msg);
        }

        private void OnTake(byte origin, LootTake m)
        {
            if (m.Owner != Coop.LocalId) return;
            var body = GameInfo.LocalBody;
            var done = new GiveDone { Token = 0, To = origin };
            if (body != null && !body.alive && _offered.TryGetValue(m.Key, out var item) && item != null && Lootable(body).Contains(item))
            {
                done.Item = ItemCodec.Encode(item);
                _offered.Remove(m.Key);
                Object.Destroy(item.gameObject);
                Log.Info("[loot] " + Coop.Player(origin).Name + " took " + item.id + " from our body");
                CoopRuntime.Notice(Lang.F("{0} took your {1}", Coop.Player(origin).Name, Medic.Pretty(item.id)));
                _nextList = 0f;  // refresh the list now
            }
            Session.Broadcast(done);
        }

        // ------------------------------------------------------------------ looter

        internal void Take(byte owner, int key)
        {
            string why = Medic.CantTreat(owner);
            var body = GameInfo.LocalBody;
            if (why == null && body.FirstEmptySlot() < 0) why = Lang.T("You have no free hand or slot.");
            if (why != null) { CoopRuntime.Notice(why); return; }
            Session.Broadcast(new LootTake { Owner = owner, Key = key });
        }
    }
}
