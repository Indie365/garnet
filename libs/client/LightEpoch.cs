﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Garnet.client
{
    /// <summary>
    /// Epoch protection
    /// </summary>
    public sealed unsafe class LightEpoch
    {
        /// Size of cache line in bytes
        private const int CacheLineBytes = 64;

        /// <summary>
        /// Default invalid index entry.
        /// </summary>
        private const int InvalidIndex = 0;

        /// <summary>
        /// Default number of entries in the entries table
        /// </summary>
        private static readonly ushort TableSize = Math.Max((ushort)128, (ushort)(Environment.ProcessorCount * 2));

        /// <summary>
        /// Default drainlist size
        /// </summary>
        private const int DrainListSize = 16;

        /// <summary>
        /// Thread protection status entries.
        /// </summary>
        Entry[] tableRaw;
        Entry* tableAligned;
#if !NET5_0_OR_GREATER
        GCHandle tableHandle;
#endif

        static readonly Entry[] threadIndex;
        static readonly Entry* threadIndexAligned;
#if !NET5_0_OR_GREATER
        static GCHandle threadIndexHandle;
#endif

        /// <summary>
        /// List of action, epoch pairs containing actions to performed 
        /// when an epoch becomes safe to reclaim. Marked volatile to
        /// ensure latest value is seen by the last suspended thread.
        /// </summary>
        volatile int drainCount = 0;
        private readonly EpochActionPair[] drainList = new EpochActionPair[DrainListSize];

        /// <summary>
        /// A thread's entry in the epoch table.
        /// </summary>
        [ThreadStatic]
        static int threadEntryIndex;

        /// <summary>
        /// Number of instances using this entry
        /// </summary>
        [ThreadStatic]
        static int threadEntryIndexCount;

        [ThreadStatic]
        static int threadId;

        [ThreadStatic]
        static ushort startOffset1;
        [ThreadStatic]
        static ushort startOffset2;

        /// <summary>
        /// Global current epoch value
        /// </summary>
        public int CurrentEpoch;

        /// <summary>
        /// Cached value of latest epoch that is safe to reclaim
        /// </summary>
        public int SafeToReclaimEpoch;

        /// <summary>
        /// Local view of current epoch, for an epoch-protected thread
        /// </summary>
        public int LocalCurrentEpoch => (*(tableAligned + threadEntryIndex)).localCurrentEpoch;

        /// <summary>
        /// Static constructor to setup shared cache-aligned space
        /// to store per-entry count of instances using that entry
        /// </summary>
        static LightEpoch()
        {
            long p;

            // Over-allocate to do cache-line alignment
#if NET5_0_OR_GREATER
            threadIndex = GC.AllocateArray<Entry>(TableSize + 2, true);
            p = (long)Unsafe.AsPointer(ref threadIndex[0]);
#else
            threadIndex = new Entry[kTableSize + 2];
            threadIndexHandle = GCHandle.Alloc(threadIndex, GCHandleType.Pinned);
            p = (long)threadIndexHandle.AddrOfPinnedObject();
#endif
            // Force the pointer to align to 64-byte boundaries
            long p2 = (p + (CacheLineBytes - 1)) & ~(CacheLineBytes - 1);
            threadIndexAligned = (Entry*)p2;
        }

        /// <summary>
        /// Instantiate the epoch table
        /// </summary>
        public LightEpoch()
        {
            long p;

#if NET5_0_OR_GREATER
            tableRaw = GC.AllocateArray<Entry>(TableSize + 2, true);
            p = (long)Unsafe.AsPointer(ref tableRaw[0]);
#else
            // Over-allocate to do cache-line alignment
            tableRaw = new Entry[kTableSize + 2];
            tableHandle = GCHandle.Alloc(tableRaw, GCHandleType.Pinned);
            p = (long)tableHandle.AddrOfPinnedObject();
#endif
            // Force the pointer to align to 64-byte boundaries
            long p2 = (p + (CacheLineBytes - 1)) & ~(CacheLineBytes - 1);
            tableAligned = (Entry*)p2;

            CurrentEpoch = 1;
            SafeToReclaimEpoch = 0;

            for (int i = 0; i < DrainListSize; i++)
                drainList[i].Epoch = int.MaxValue;
            drainCount = 0;
        }

        /// <summary>
        /// Clean up epoch table
        /// </summary>
        public void Dispose()
        {
#if !NET5_0_OR_GREATER
            tableHandle.Free();
#endif
            CurrentEpoch = 1;
            SafeToReclaimEpoch = 0;
        }

        /// <summary>
        /// Check whether current epoch instance is protected on this thread
        /// </summary>
        /// <returns>Result of the check</returns>
        public bool ThisInstanceProtected()
        {
            int entry = threadEntryIndex;
            if (InvalidIndex != entry)
            {
                if ((*(tableAligned + entry)).threadId == entry)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Check whether any epoch instance is protected on this thread
        /// </summary>
        /// <returns>Result of the check</returns>
        public static bool AnyInstanceProtected()
        {
            int entry = threadEntryIndex;
            if (InvalidIndex != entry)
            {
                return threadEntryIndexCount > 0;
            }
            return false;
        }

        /// <summary>
        /// Enter the thread into the protected code region
        /// </summary>
        /// <returns>Current epoch</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ProtectAndDrain()
        {
            int entry = threadEntryIndex;

            (*(tableAligned + entry)).threadId = threadEntryIndex;
            (*(tableAligned + entry)).localCurrentEpoch = CurrentEpoch;

            if (drainCount > 0)
            {
                Drain((*(tableAligned + entry)).localCurrentEpoch);
            }

            return (*(tableAligned + entry)).localCurrentEpoch;
        }

        /// <summary>
        /// Thread suspends its epoch entry
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Suspend()
        {
            Release();
            if (drainCount > 0) SuspendDrain();
        }

        /// <summary>
        /// Thread resumes its epoch entry
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resume()
        {
            Acquire();
            ProtectAndDrain();
        }

        /// <summary>
        /// Thread resumes its epoch entry
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resume(out int resumeEpoch)
        {
            Acquire();
            resumeEpoch = ProtectAndDrain();
        }

        /// <summary>
        /// Increment current epoch and associate trigger action
        /// with the prior epoch
        /// </summary>
        /// <param name="onDrain">Trigger action</param>
        /// <returns></returns>
        public void BumpCurrentEpoch(Action onDrain)
        {
            int priorEpoch = BumpCurrentEpoch() - 1;

            int i = 0;
            while (true)
            {
                if (drainList[i].Epoch == int.MaxValue)
                {
                    if (Interlocked.CompareExchange(ref drainList[i].Epoch, int.MaxValue - 1, int.MaxValue) == int.MaxValue)
                    {
                        drainList[i].Action = onDrain;
                        drainList[i].Epoch = priorEpoch;
                        Interlocked.Increment(ref drainCount);
                        break;
                    }
                }
                else
                {
                    var triggerEpoch = drainList[i].Epoch;

                    if (triggerEpoch <= SafeToReclaimEpoch)
                    {
                        if (Interlocked.CompareExchange(ref drainList[i].Epoch, int.MaxValue - 1, triggerEpoch) == triggerEpoch)
                        {
                            var triggerAction = drainList[i].Action;
                            drainList[i].Action = onDrain;
                            drainList[i].Epoch = priorEpoch;
                            triggerAction();
                            break;
                        }
                    }
                }

                if (++i == DrainListSize)
                {
                    ProtectAndDrain();
                    i = 0;
                    Thread.Yield();
                }
            }

            ProtectAndDrain();
        }

        /// <summary>
        /// Mechanism for threads to mark some activity as completed until
        /// some version by this thread
        /// </summary>
        /// <param name="markerIdx">ID of activity</param>
        /// <param name="version">Version</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Mark(int markerIdx, long version)
        {
            (*(tableAligned + threadEntryIndex)).markers[markerIdx] = (int)version;
        }

        /// <summary>
        /// Check if all active threads have completed the some
        /// activity until given version.
        /// </summary>
        /// <param name="markerIdx">ID of activity</param>
        /// <param name="version">Version</param>
        /// <returns>Whether complete</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CheckIsComplete(int markerIdx, long version)
        {
            // check if all threads have reported complete
            for (int index = 1; index <= TableSize; ++index)
            {
                int entryEpoch = (*(tableAligned + index)).localCurrentEpoch;
                int fcVersion = (*(tableAligned + index)).markers[markerIdx];
                if (entryEpoch != 0)
                {
                    if ((fcVersion != (int)version) && (entryEpoch < int.MaxValue))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Increment global current epoch
        /// </summary>
        /// <returns></returns>
        int BumpCurrentEpoch()
        {
            int nextEpoch = Interlocked.Add(ref CurrentEpoch, 1);

            if (drainCount > 0)
                Drain(nextEpoch);

            return nextEpoch;
        }

        /// <summary>
        /// Looks at all threads and return the latest safe epoch
        /// </summary>
        /// <param name="currentEpoch">Current epoch</param>
        /// <returns>Safe epoch</returns>
        int ComputeNewSafeToReclaimEpoch(int currentEpoch)
        {
            int oldestOngoingCall = currentEpoch;

            for (int index = 1; index <= TableSize; ++index)
            {
                int entryEpoch = (*(tableAligned + index)).localCurrentEpoch;
                if (0 != entryEpoch)
                {
                    if (entryEpoch < oldestOngoingCall)
                    {
                        oldestOngoingCall = entryEpoch;
                    }
                }
            }

            // The latest safe epoch is the one just before 
            // the earliest unsafe epoch.
            SafeToReclaimEpoch = oldestOngoingCall - 1;
            return SafeToReclaimEpoch;
        }

        /// <summary>
        /// Take care of pending drains after epoch suspend
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SuspendDrain()
        {
            while (drainCount > 0)
            {
                // Barrier ensures we see the latest epoch table entries. Ensures
                // that the last suspended thread drains all pending actions.
                Thread.MemoryBarrier();
                for (int index = 1; index <= TableSize; ++index)
                {
                    int entryEpoch = (*(tableAligned + index)).localCurrentEpoch;
                    if (0 != entryEpoch)
                    {
                        return;
                    }
                }
                Resume();
                Release();
            }
        }

        /// <summary>
        /// Check and invoke trigger actions that are ready
        /// </summary>
        /// <param name="nextEpoch">Next epoch</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Drain(int nextEpoch)
        {
            ComputeNewSafeToReclaimEpoch(nextEpoch);

            for (int i = 0; i < DrainListSize; i++)
            {
                var triggerEpoch = drainList[i].Epoch;

                if (triggerEpoch <= SafeToReclaimEpoch)
                {
                    if (Interlocked.CompareExchange(ref drainList[i].Epoch, int.MaxValue - 1, triggerEpoch) == triggerEpoch)
                    {
                        var triggerAction = drainList[i].Action;
                        drainList[i].Action = null;
                        drainList[i].Epoch = int.MaxValue;
                        Interlocked.Decrement(ref drainCount);
                        triggerAction();
                        if (drainCount == 0) break;
                    }
                }
            }
        }

        /// <summary>
        /// Thread acquires its epoch entry
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Acquire()
        {
            if (threadEntryIndex == InvalidIndex)
                threadEntryIndex = ReserveEntryForThread();

            Debug.Assert((*(tableAligned + threadEntryIndex)).localCurrentEpoch == 0,
                "Trying to acquire protected epoch. Make sure you do not re-enter Tsavorite from callbacks or IDevice implementations. If using tasks, use TaskCreationOptions.RunContinuationsAsynchronously.");

            threadEntryIndexCount++;
        }

        /// <summary>
        /// Thread releases its epoch entry
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Release()
        {
            int entry = threadEntryIndex;

            Debug.Assert((*(tableAligned + entry)).localCurrentEpoch != 0,
                "Trying to release unprotected epoch. Make sure you do not re-enter Tsavorite from callbacks or IDevice implementations. If using tasks, use TaskCreationOptions.RunContinuationsAsynchronously.");

            (*(tableAligned + entry)).localCurrentEpoch = 0;
            (*(tableAligned + entry)).threadId = 0;

            threadEntryIndexCount--;
            if (threadEntryIndexCount == 0)
            {
                (threadIndexAligned + threadEntryIndex)->threadId = 0;
                threadEntryIndex = InvalidIndex;
            }
        }

        /// <summary>
        /// Reserve entry for thread. This method relies on the fact that no
        /// thread will ever have ID 0.
        /// </summary>
        /// <returns>Reserved entry</returns>
        static int ReserveEntry()
        {
            while (true)
            {
                // Try to acquire entry
                if (0 == (threadIndexAligned + startOffset1)->threadId)
                {
                    if (0 == Interlocked.CompareExchange(
                        ref (threadIndexAligned + startOffset1)->threadId,
                        threadId, 0))
                        return startOffset1;
                }

                if (startOffset2 > 0)
                {
                    // Try alternate entry
                    startOffset1 = startOffset2;
                    startOffset2 = 0;
                }
                else startOffset1++; // Probe next sequential entry
                if (startOffset1 > TableSize)
                {
                    startOffset1 -= TableSize;
                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// Allocate a new entry in epoch table. This is called 
        /// once for a thread.
        /// </summary>
        /// <returns>Reserved entry</returns>
        static int ReserveEntryForThread()
        {
            if (threadId == 0) // run once per thread for performance
            {
                threadId = Environment.CurrentManagedThreadId;
                uint code = (uint)Utility.Murmur3(threadId);
                startOffset1 = (ushort)(1 + (code % TableSize));
                startOffset2 = (ushort)(1 + ((code >> 16) % TableSize));
            }
            return ReserveEntry();
        }

        /// <summary>
        /// Epoch table entry (cache line size).
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = CacheLineBytes)]
        struct Entry
        {
            /// <summary>
            /// Thread-local value of epoch
            /// </summary>
            [FieldOffset(0)]
            public int localCurrentEpoch;

            /// <summary>
            /// ID of thread associated with this entry.
            /// </summary>
            [FieldOffset(4)]
            public int threadId;

            [FieldOffset(8)]
            public int reentrant;

            [FieldOffset(12)]
            public fixed int markers[13];
        };

        struct EpochActionPair
        {
            public long Epoch;
            public Action Action;
        }
    }
}