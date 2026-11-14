using System.Collections.Generic;
using CCoop.Net.Session;
using CCoop.Plugin.Protocol;
using CCoop.Plugin.Runtime;
using CCoop.Plugin.World;
using UnityEngine;

namespace CCoop.Plugin.Sync
{
    /// <summary>
    /// Handing an item to a teammate. The giver offers what's in their hand; the receiver
    /// gets a prompt; only once they accept (and have room) does the item leave the giver
    /// and arrive, with its full state, in the receiver's inventory. Nothing is ever
    /// duplicated or lost: if the giver no longer has it by then, nothing arrives.
    /// </summary>
    internal sealed class GiveSync : CoopModule
    {
        private const float OfferLifetime = 20f;

        private uint _nextToken = 1;
        private readonly Dictionary<uint, Item> _outgoing = new Dictionary<uint, Item>();

        private struct Incoming
        {
            public uint Token;
            public byte From;
            public string ItemId;
            public float At;
        }

        private readonly List<Incoming> _incoming = new List<Incoming>();
        private static GUIStyle _box;

        /// <summary>Dev: accept every offer at once.</summary>
        internal static bool DevAutoAccept;

        internal override void Attach(NetSession s)
        {
            s.On<GiveOffer>(OnOffer);
            s.On<GiveAnswer>(OnAnswer);
            s.On<GiveDone>(OnDone);
        }

        internal override void Detach()
        {
            _outgoing.Clear();
            _incoming.Clear();
        }

        internal override void OnWorldLost() => _incoming.Clear();

        // ------------------------------------------------------------------ giver

        internal void Offer(byte to)
        {
            string why = Medic.CantTreat(to);
            var item = Medic.HeldItem(GameInfo.LocalBody);
            if (why == null && item == null) why = "Hold the item you want to give.";
            if (why != null) { CoopRuntime.Notice(why); return; }
            uint token = _nextToken++;
            _outgoing[token] = item;
            Session.Broadcast(new GiveOffer { Token = token, To = to, ItemId = item.id });
            CoopRuntime.Notice("Offered " + Medic.Pretty(item.id) + " to " + Coop.Player(to).Name);
            Log.Info("[give] offering " + item.id + " to " + Coop.Player(to).Name);
        }

        private void OnAnswer(byte origin, GiveAnswer m)
        {
            if (m.Giver != Coop.LocalId || !_outgoing.TryGetValue(m.Token, out var item)) return;
            _outgoing.Remove(m.Token);
            string who = Coop.Player(origin).Name;
            if (!m.Accept)
            {
                CoopRuntime.Notice(who + (string.IsNullOrEmpty(m.Reason) ? " said no thanks" : ": " + m.Reason));
                return;
            }
            var body = GameInfo.LocalBody;
            bool stillMine = item != null && body != null && item.transform.IsChildOf(body.transform.root);
            var done = new GiveDone { Token = m.Token, To = origin };
            if (stillMine)
            {
                done.Item = ItemCodec.Encode(item);
                Object.Destroy(item.gameObject);
                CoopRuntime.Notice("Gave " + Medic.Pretty(item.id) + " to " + who);
            }
            else CoopRuntime.Notice("You no longer have what you offered " + who);
            Session.Broadcast(done);
        }

        // ------------------------------------------------------------------ receiver

        private void OnOffer(byte origin, GiveOffer m)
        {
            if (m.To != Coop.LocalId || origin == Coop.LocalId) return;
            _incoming.RemoveAll(i => i.From == origin);
            _incoming.Add(new Incoming { Token = m.Token, From = origin, ItemId = m.ItemId, At = Time.unscaledTime });
            if (DevAutoAccept) Answer(_incoming[_incoming.Count - 1], true);
        }

        private void Answer(Incoming offer, bool accept)
        {
            _incoming.RemoveAll(i => i.Token == offer.Token && i.From == offer.From);
            var body = GameInfo.LocalBody;
            string reason = "";
            if (accept && (body == null || !body.alive)) { accept = false; reason = "can't take it right now"; }
            else if (accept && body.FirstEmptySlot() < 0) { accept = false; reason = "has no free hand or slot"; }
            Session.Broadcast(new GiveAnswer { Token = offer.Token, Giver = offer.From, Accept = accept, Reason = reason });
            if (!accept && reason.Length > 0) CoopRuntime.Notice("You " + reason.Replace("has ", "have "));
        }

        private void OnDone(byte origin, GiveDone m)
        {
            if (m.To != Coop.LocalId) return;
            var spec = ItemCodec.Decode(m.Item);
            var body = GameInfo.LocalBody;
            if (spec == null || string.IsNullOrEmpty(spec.Id) || body == null)
            {
                CoopRuntime.Notice(Coop.Player(origin).Name + " no longer has it");
                return;
            }
            var prefab = Resources.Load(spec.Id) as GameObject;
            if (prefab == null) return;
            GameObject go = null;
            Coop.AsRemote(() => go = Object.Instantiate(prefab, body.transform.position, Quaternion.identity));
            var item = go != null ? go.GetComponent<Item>() : null;
            if (item == null) { if (go != null) Object.Destroy(go); return; }
            Coop.AsRemote(() =>
            {
                ItemCodec.Apply(item, spec);
                body.AutoPickUpItem(item);
            });
            CoopRuntime.Notice(m.Token == 0
                ? "You took " + Medic.Pretty(spec.Id) + " from " + Coop.Player(origin).Name
                : Coop.Player(origin).Name + " gave you " + Medic.Pretty(spec.Id));
            Log.Info("[give] received " + spec.Id + " from " + Coop.Player(origin).Name);
        }

        /// <summary>Accept / decline prompts, newest first, under the notices.</summary>
        internal override void Gui()
        {
            if (_incoming.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var o in _incoming.ToArray())
                if (now - o.At > OfferLifetime) Answer(o, false);
            if (_incoming.Count == 0) return;
            if (_box == null) _box = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.UpperCenter };

            float k = UI.CoopWindow.Scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(k, k, 1f));
            try
            {
                float w = Screen.width / k;
                float y = 170f;
                foreach (var o in _incoming.ToArray())
                {
                    var r = new Rect(w / 2f - 190f, y, 380f, 66f);
                    GUI.Box(r, Coop.Player(o.From).Name + " offers you " + Medic.Pretty(o.ItemId), _box);
                    if (GUI.Button(new Rect(r.x + 40f, r.y + 32f, 140f, 26f), "Take it")) Answer(o, true);
                    if (GUI.Button(new Rect(r.x + 200f, r.y + 32f, 140f, 26f), "No thanks")) Answer(o, false);
                    y += 74f;
                }
            }
            finally { GUI.matrix = old; }
        }
    }
}
