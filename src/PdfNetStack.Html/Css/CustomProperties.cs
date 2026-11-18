namespace PdfNetStack.Html.Css;

/// <summary>
/// The custom properties of a computed style (CSS Custom Properties 1, 2): an immutable hash array
/// mapped trie. Every element inherits all of its parent's custom properties, and one that sets
/// its own shares everything with the parent's map except one path of small nodes. A dictionary
/// copied per element cost 3.4 GB on a document with 4,000 custom properties on the root and
/// 60,000 elements each setting one; the trie costs a few hundred bytes per element.
/// </summary>
internal sealed class CustomProperties
{
    public static readonly CustomProperties Empty = new(Node.Empty);

    private readonly Node _root;

    private CustomProperties(Node root) => _root = root;

    /// <summary>The value of a custom property, or null when it is not set or was made invalid.</summary>
    public List<CssValue>? Find(string name) => _root.Find(Hash(name), name);

    /// <summary>
    /// A map with <paramref name="name"/> set to <paramref name="value"/>; a null value marks the
    /// property invalid, as a cycle does, so <see cref="Find"/> no longer returns the inherited one.
    /// </summary>
    public CustomProperties With(string name, List<CssValue>? value) => new(_root.With(Hash(name), 0, name, value));

    // The ordinal hash is randomized per process on .NET, so hostile names cannot be chosen to collide.
    private static int Hash(string name) => StringComparer.Ordinal.GetHashCode(name);

    private static int PopCount(uint bits)
    {
#if NET8_0_OR_GREATER
        return System.Numerics.BitOperations.PopCount(bits);
#else
        bits -= (bits >> 1) & 0x55555555;
        bits = (bits & 0x33333333) + ((bits >> 2) & 0x33333333);
        return (int)((((bits + (bits >> 4)) & 0x0F0F0F0F) * 0x01010101) >> 24);
#endif
    }

    /// <summary>The slot of a hash at a level: 5 bits of it, from the low end.</summary>
    private static int Slot(int hash, int shift) => (int)(((uint)hash >> shift) & 31);

    /// <summary>A trie node: a bitmap of the 32 slots in use and, in slot order, a leaf or a child node for each.</summary>
    private sealed class Node(uint bitmap, object[] slots)
    {
        public static readonly Node Empty = new(0, []);

        private uint Bitmap { get; } = bitmap;

        private object[] Slots { get; } = slots;

        public List<CssValue>? Find(int hash, string name)
        {
            Node node = this;
            for (int shift = 0; ; shift += 5)
            {
                uint bit = 1u << Slot(hash, shift);
                if ((node.Bitmap & bit) == 0) return null;
                object slot = node.Slots[PopCount(node.Bitmap & (bit - 1))];
                if (slot is Leaf leaf) return leaf.Find(hash, name);
                node = (Node)slot;
            }
        }

        public Node With(int hash, int shift, string name, List<CssValue>? value)
        {
            uint bit = 1u << Slot(hash, shift);
            int index = PopCount(Bitmap & (bit - 1));
            if ((Bitmap & bit) == 0)
            {
                var added = new object[Slots.Length + 1];
                Array.Copy(Slots, 0, added, 0, index);
                added[index] = new Leaf(hash, [name], [value]);
                Array.Copy(Slots, index, added, index + 1, Slots.Length - index);
                return new Node(Bitmap | bit, added);
            }

            object replacement = Slots[index] switch
            {
                Node child => child.With(hash, shift + 5, name, value),
                Leaf leaf when leaf.Hash == hash => leaf.With(name, value),
                Leaf leaf => Split(leaf, new Leaf(hash, [name], [value]), shift + 5),
                _ => throw new InvalidOperationException("A trie slot holds a leaf or a node."),
            };
            var copy = (object[])Slots.Clone();
            copy[index] = replacement;
            return new Node(Bitmap, copy);
        }

        // Two leaves with different hashes that share a slot: one more level until their 5 bit
        // slots differ, which happens by the last level since the 32 bit hashes differ.
        private static Node Split(Leaf a, Leaf b, int shift)
        {
            int sa = Slot(a.Hash, shift), sb = Slot(b.Hash, shift);
            if (sa == sb) return new Node(1u << sa, [Split(a, b, shift + 5)]);
            return new Node((1u << sa) | (1u << sb), sa < sb ? [a, b] : [b, a]);
        }
    }

    /// <summary>The properties whose names share one hash: nearly always one.</summary>
    private sealed class Leaf(int hash, string[] names, List<CssValue>?[] values)
    {
        public int Hash { get; } = hash;

        public List<CssValue>? Find(int hash, string name)
        {
            if (hash != Hash) return null;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return values[i];
            }

            return null;
        }

        public Leaf With(string name, List<CssValue>? value)
        {
            int at = 0;
            while (at < names.Length && !string.Equals(names[at], name, StringComparison.Ordinal)) at++;
            if (at == names.Length)
            {
                string[] moreNames = new string[names.Length + 1];
                var moreValues = new List<CssValue>?[values.Length + 1];
                names.CopyTo(moreNames, 0);
                values.CopyTo(moreValues, 0);
                moreNames[names.Length] = name;
                moreValues[values.Length] = value;
                return new Leaf(Hash, moreNames, moreValues);
            }

            var changed = (List<CssValue>?[])values.Clone();
            changed[at] = value;
            return new Leaf(Hash, names, changed);
        }
    }
}
