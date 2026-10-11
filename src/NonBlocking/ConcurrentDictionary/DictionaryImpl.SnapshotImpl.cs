// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable

using System;
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

                // Keeps the sweeper from retiring slots of this table until Dispose, the end of the
                // enumeration or the generation expires. The count is updated with a
                // compare-exchange, a full fence, which the sweeper's check relies on.
                _counted = this._table.TryOpenEnumeration(out _generation);
            }

            // Whether this enumeration is counted, and in which generation.
            private bool _counted;
            private int _generation;

            private void Release()
            {
                if (_counted)
                {
                    _counted = false;
                    this._table.CloseEnumeration(_generation);
                }
            }

            public override void Dispose() => Release();

            // Moves the count to the newest generation, so that an enumeration in use is not left
            // in one the sweeper is about to drop. The new count is taken before the old one is
            // given up, so the enumeration counts throughout.
            private void Renew()
            {
                if (this._table.TryOpenEnumeration(out int generation))
                {
                    int old = _generation;
                    _generation = generation;
                    this._table.CloseEnumeration(old);
                }
            }

            public override int Count => _table.Count;

            public override bool MoveNext()
            {
                if (_counted && Volatile.Read(ref this._table._enumerationGeneration) != _generation)
                {
                    Renew();
                }

                var entries = this._table._entries;
                while (_idx < entries.Length)
                {
                    ref var nextEntry = ref entries[_idx++];

                    // An insert writes the key before it CASes in a live value, so the value must be
                    // read first: a copy of the whole entry can pair a new value with the key from
                    // before the insert wrote it.
                    object nextV = Volatile.Read(ref nextEntry.value);
                    if (nextV == null || nextV == TOMBSTONE)
                    {
                        // A tombstone holds no value: a put of the same key reuses the slot, and a
                        // copy turns it into TOMBPRIME before the key can appear in a newer table.
                        continue;
                    }

                    if (nextV == TOMBPRIME && Volatile.Read(ref this._table._newTable) == null)
                    {
                        // TOMBPRIME with no resize in progress marks a removed key the sweeper is
                        // retiring; a copy only writes it once a new table exists. The key may be
                        // retired and added back in a later slot while this enumeration runs, so a
                        // lookup from here could yield it a second time.
                        continue;
                    }

                    var nextKstore = nextEntry.key;
                    if (nextKstore == null)
                    {
                        // slot was deleted.
                        continue;
                    }

                    _curKey = _table.keyFromEntry(nextKstore);
                    if (nextV is Prime)
                    {
                        if (nextV == TOMBPRIME)
                        {
                            // A copy writes TOMBPRIME over a slot with no value, which can be a slot an
                            // insert has claimed by its hash but not yet given a key. Its key then reads
                            // as the empty key: 0 for integer keys, which the lookup below would resolve
                            // to a real key 0 stored elsewhere. The hash is written before the key, so a
                            // key that does not match the slot's hash was never written to this slot.
                            // The slot may also be retired by a sweep that started before this
                            // enumeration and raced the resize. A retired slot gets a dead hash before
                            // its key is cleared, and a key added back can only take a later slot once
                            // that hash is visible, so the hash must still match after the lookup too.
                            int keyHash = _table.hash(_curKey);
                            if (nextEntry.hash != keyHash)
                            {
                                continue;
                            }

                            nextV = _table.TryGetValue(_curKey);
                            Interlocked.MemoryBarrier();
                            if (Volatile.Read(ref nextEntry.hash) != keyHash)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            // A copy is in progress: resolve through a lookup, which follows
                            // forwarding to the newer table.
                            nextV = _table.TryGetValue(_curKey);
                        }

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
                Release();
                return false;
            }

            public override void Reset()
            {
                Release();
                _counted = this._table.TryOpenEnumeration(out _generation);

                _idx = 0;
                _curKey = default;
                _curValue = default;
            }
        }
    }
}
