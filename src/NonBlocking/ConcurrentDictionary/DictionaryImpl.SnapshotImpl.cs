// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable

using System.Runtime.CompilerServices;
using System.Threading;

namespace NonBlocking
{
    internal abstract partial class DictionaryImpl<TKey, TKeyStore, TValue>
        : DictionaryImpl<TKey, TValue>
    {
        internal override Snapshot GetSnapshot()
        {
            return new SnapshotImpl(this);
        }

        private class SnapshotImpl : Snapshot
        {
            private readonly DictionaryImpl<TKey, TKeyStore, TValue> _table;

            public SnapshotImpl(DictionaryImpl<TKey, TKeyStore, TValue> dict)
            {
                this._table = dict;

                // linearization point.
                // if table is quiescent and has no copy in progress,
                // we can simply iterate over its table.
                while (_table._newTable != null)
                {
                    // there is a copy in progress, finish it and try again
                    _table.HelpCopy(copy_all: true);
                    this._table = (DictionaryImpl<TKey, TKeyStore, TValue>)this._table._topDict._table;
                }
            }

            public override int Count => _table.Count;

            public override bool MoveNext()
            {
                var entries = this._table._entries;
                while (_idx < entries.Length)
                {
                    ref var nextEntry = ref entries[_idx++];

                    // An insert CASes a live value in after writing the key, so a live value implies the key is visible.
                    object nextV = Volatile.Read(ref nextEntry.value);
                    if (nextV == null || nextV == TOMBSTONE)
                    {
                        continue;
                    }

                    var nextKstore = nextEntry.key;
                    if (nextKstore == null)
                    {
                        // slot was deleted and swept.
                        continue;
                    }

                    _curKey = _table.keyFromEntry(nextKstore);
                    if (nextV is Prime)
                    {
                        // A copy writes TOMBPRIME over a slot with no value, which can be a slot an
                        // insert has claimed by its hash but not yet given a key. Only a live value
                        // implies a visible key, so here the key can still read as the empty key:
                        // 0 for integer keys, which would resolve to a real key 0 stored elsewhere.
                        // The hash is written before the key, so a key that does not match the
                        // slot's hash was never written to this slot.
                        if (nextV == TOMBPRIME && nextEntry.hash != _table.hash(_curKey))
                        {
                            continue;
                        }

                        // a copy or sweep is in progress: resolve through the table, which follows forwarding.
                        nextV = _table.TryGetValue(_curKey);
                        if (nextV == null)
                        {
                            continue;
                        }
                    }

                    _curValue = _table.FromObjectValue(nextV);
                    return true;
                }

                _curKey = default;
                _curValue = default;
                return false;
            }

            public override void Reset()
            {
                _idx = 0;
                _curKey = default;
                _curValue = default;
            }
        }
    }
}
