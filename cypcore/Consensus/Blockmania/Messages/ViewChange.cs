﻿// CYPCore by Matthew Hellyer is licensed under CC BY-NC-ND 4.0.
// To view a copy of this license, visit https://creativecommons.org/licenses/by-nc-nd/4.0

using System;

namespace CYPCore.Consensus.BlockMania.Messages
{
    public class ViewChange : IMessage
    {
        public string Hash { get; }
        public ulong Node { get; }
        public ulong Round { get; }
        public ulong Sender { get; }
        public uint View { get; }

        public ViewChange(string hash, ulong node, ulong round, ulong sender, uint view)
        {
            Hash = hash;
            Node = node;
            Round = round;
            Sender = sender;
            View = view;
        }

        public MessageKind Kind()
        {
            return MessageKind.ViewChangedMsg;
        }

        public Tuple<ulong, ulong> NodeRound()
        {
            return Tuple.Create(Node, Round);
        }

        public override string ToString()
        {
            return $"view-change{{node: {Node}, round: {Round}, view: {View}, hash: '{Util.FmtHash(Hash):S}', sender: {Sender}}}";
        }
    }
}
