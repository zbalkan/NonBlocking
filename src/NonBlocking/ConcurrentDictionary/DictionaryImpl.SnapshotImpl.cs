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
                    var nextEntry = entries[_idx++];

                    if (nextEntry.value != null)
                    {
                        // A tombstone holds no value: a put of the same key reuses the slot, and a
                        // copy turns it into TOMBPRIME before the key can appear in a newer table.
                        // TOMBPRIME with no resize in progress marks a removed key the sweeper is
                        // retiring; a copy only writes it once a new table exists. Either way the
                        // key may be retired and added back in a later slot while this enumeration
                        // runs, so a lookup from here could yield it a second time.
                        if (nextEntry.value == TOMBSTONE ||
                            (nextEntry.value == TOMBPRIME && Volatile.Read(ref this._table._newTable) == null))
                        {
                            continue;
                        }

                        var nextKstore = nextEntry.key;
                        if (nextKstore == null)
                        {
                            // slot was deleted.
                            continue;
                        }

                        _curKey = _table.keyFromEntry(nextKstore);
                        object nextV;
                        if (nextEntry.value == TOMBPRIME)
                        {
                            // A resize is in progress, and the slot was either copied or is being
                            // retired by a sweep that started before this enumeration and raced the
                            // resize. A retired slot gets a dead hash before its key is cleared, and a
                            // key added back can only take a later slot once that hash is visible. So
                            // the slot stands for the key only if its hash still matches the key both
                            // before and after the lookup.
                            int keyHash = _table.hash(_curKey);
                            if (nextEntry.hash != keyHash)
                            {
                                continue;
                            }

                            nextV = _table.TryGetValue(_curKey);
                            Interlocked.MemoryBarrier();
                            if (Volatile.Read(ref entries[_idx - 1].hash) != keyHash)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            nextV = _table.TryGetValue(_curKey);
                        }

                        if (nextV != null)
                        {
                            _curValue = _table.FromObjectValue(nextV);
                            return true;
                        }
                    }
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
