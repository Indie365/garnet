﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Garnet.common;
using Garnet.server;
using Microsoft.Extensions.Logging;
using Tsavorite.core;

namespace Garnet.cluster
{
    using BasicGarnetApi = GarnetApi<BasicContext<SpanByte, SpanByte, SpanByte, SpanByteAndMemory, long, MainStoreFunctions>, BasicContext<byte[], IGarnetObject, SpanByte, GarnetObjectStoreOutput, long, ObjectStoreFunctions>>;

    /// <summary>
    /// Cluster manager
    /// </summary>
    internal sealed partial class ClusterManager : IDisposable
    {
        /// <summary>
        /// Try to add slots to local worker
        /// </summary>
        /// <param name="slots">Slot list</param>
        /// <param name="slotAssigned">Slot number of already assigned slot</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryAddSlots(HashSet<int> slots, out int slotAssigned)
        {
            slotAssigned = -1;
            while (true)
            {
                var current = currentConfig;
                if (current.NumWorkers == 0) return false;

                if (!currentConfig.TryAddSlots(slots, out var slot, out var newConfig))
                {
                    slotAssigned = slot;
                    return false;
                }
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }
            FlushConfig();
            logger?.LogTrace("ADD SLOTS {slots}", GetRange(slots.ToArray()));
            return true;
        }

        /// <summary>
        /// Try to remove ownernship of slots. Slot state transition to OFFLINE.
        /// </summary>
        /// <param name="slots">Slot list</param>
        /// <param name="notLocalSlot">The slot number that is not local.</param>
        /// <returns><see langword="false"/> if a slot provided is not local; otherwise <see langword="true"/>.</returns>
        public bool TryRemoveSlots(HashSet<int> slots, out int notLocalSlot)
        {
            notLocalSlot = -1;

            while (true)
            {
                var current = currentConfig;
                if (current.NumWorkers == 0) return false;

                if (!currentConfig.TryRemoveSlots(slots, out var slot, out var newConfig) &&
                    slot != -1)
                {
                    notLocalSlot = slot;
                    return false;
                }
                newConfig = newConfig.BumpLocalNodeConfigEpoch();
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }
            FlushConfig();
            logger?.LogTrace("REMOVE SLOTS {slots}", string.Join(",", slots));
            return true;
        }

        /// <summary>
        /// Try to prepare node for migration of slot to node with specified node Id.
        /// </summary>
        /// <param name="slot">Slot to change state</param>
        /// <param name="nodeid">Migration target node-id</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryPrepareSlotForMigration(int slot, string nodeid, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            while (true)
            {
                var current = currentConfig;
                var migratingWorkerId = current.GetWorkerIdFromNodeId(nodeid);

                if (migratingWorkerId == 0)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                    return false;
                }

                if (current.GetLocalNodeId().Equals(nodeid))
                {
                    errorMessage = CmdStrings.RESP_ERR_GENERIC_MIGRATE_TO_MYSELF;
                    return false;
                }

                if (current.GetNodeRoleFromNodeId(nodeid) != NodeRole.PRIMARY)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Target node {nodeid} is not a master node.");
                    return false;
                }

                if (!current.IsLocal((ushort)slot))
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I'm not the owner of hash slot {slot}");
                    return false;
                }

                if (current.GetState((ushort)slot) != SlotState.STABLE)
                {
                    var _migratingNodeId = current.GetNodeIdFromSlot((ushort)slot);
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Slot already scheduled for migration from {_migratingNodeId}");
                    return false;
                }

                // Slot is conditionally assigned to target node
                // Redirection logic should be aware of this and not consider this slot as part of target node until migration completes
                // Cluster status queries should also be aware of this implicit assignment and return this node as the current owner
                // The above is only true for the primary that owns this slot and this configuration change is not propagated through gossip.
                var newConfig = current.UpdateSlotState(slot, migratingWorkerId, SlotState.MIGRATING);
                if (newConfig == null)
                {
                    errorMessage = CmdStrings.RESP_ERR_GENERIC_SLOTSTATE_TRANSITION;
                    return false;
                }

                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }
            FlushConfig();

            logger?.LogInformation("MIGRATE {slot} TO {currentConfig.GetWorkerAddressFromNodeId(nodeid)}", slot, currentConfig.GetWorkerAddressFromNodeId(nodeid));
            return true;
        }

        /// <summary>
        /// Try to change list of slots to migrating state
        /// </summary>
        /// <param name="slots">Slot list</param>
        /// <param name="nodeid">Migration target node-id</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryPrepareSlotsForMigration(HashSet<int> slots, string nodeid, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            while (true)
            {
                var current = currentConfig;
                var migratingWorkerId = current.GetWorkerIdFromNodeId(nodeid);

                //Check migrating worker is a known valid worker
                if (migratingWorkerId == 0)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                    return false;
                }

                //Check if nodeid is different from local node
                if (current.GetLocalNodeId().Equals(nodeid))
                {
                    errorMessage = CmdStrings.RESP_ERR_GENERIC_MIGRATE_TO_MYSELF;
                    return false;
                }

                //Check if local node is primary
                if (current.GetNodeRoleFromNodeId(nodeid) != NodeRole.PRIMARY)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Target node {nodeid} is not a master node.");
                    return false;
                }

                foreach (var slot in slots)
                {
                    //Check if slot is owned by local node
                    if (!current.IsLocal((ushort)slot))
                    {
                        errorMessage = Encoding.ASCII.GetBytes($"ERR I'm not the owner of hash slot {slot}");
                        return false;
                    }

                    //Check node state is stable
                    if (current.GetState((ushort)slot) != SlotState.STABLE)
                    {
                        var _migratingNodeId = current.GetNodeIdFromSlot((ushort)slot);
                        errorMessage = Encoding.ASCII.GetBytes($"ERR Slot already scheduled for migration from {_migratingNodeId}");
                        return false;
                    }
                }

                // Slot is conditionally assigned to target node
                // Redirection logic should be aware of this and not consider this slot as part of target node until migration completes
                // Cluster status queries should also be aware of this implicit assignment and return this node as the current owner
                // The above is only true for the primary that owns this slot and this configuration change is not propagated through gossip.
                var newConfig = currentConfig.UpdateMultiSlotState(slots, migratingWorkerId, SlotState.MIGRATING);
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }
            FlushConfig();

            logger?.LogInformation("MIGRATE {slot} TO {migrating node}", string.Join(' ', slots), currentConfig.GetWorkerAddressFromNodeId(nodeid));
            return true;
        }

        /// <summary>
        /// Try to prepare node for import of slot from node with specified nodeid.
        /// </summary>
        /// <param name="slot">Slot list</param>
        /// <param name="nodeid">Importing source node-id</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>       
        public bool TryPrepareSlotForImport(int slot, string nodeid, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            while (true)
            {
                var current = currentConfig;
                var importingWorkerId = current.GetWorkerIdFromNodeId(nodeid);
                if (importingWorkerId == 0)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                    return false;
                }

                if (current.GetLocalNodeRole() != NodeRole.PRIMARY)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Importing node {current.GetLocalNodeRole()} is not a master node.");
                    return false;
                }

                if (current.IsLocal((ushort)slot, readCommand: false))
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR This is a local hash slot {slot} and is already imported");
                    return false;
                }

                string sourceNodeId = current.GetNodeIdFromSlot((ushort)slot);
                if (sourceNodeId == null || !sourceNodeId.Equals(nodeid))
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Slot {slot} is not owned by {nodeid}");
                    return false;
                }

                if (current.GetState((ushort)slot) != SlotState.STABLE)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Slot already scheduled for import from {nodeid}");
                    return false;
                }

                var newConfig = current.UpdateSlotState(slot, importingWorkerId, SlotState.IMPORTING);
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }
            FlushConfig();

            logger?.LogInformation("IMPORT {slot} FROM {currentConfig.GetWorkerAddressFromNodeId(nodeid)}", slot, currentConfig.GetWorkerAddressFromNodeId(nodeid));
            return true;
        }

        /// <summary>
        /// Try to prepare node for import of slots from node with specified nodeid.
        /// </summary>
        /// <param name="slots">Slot list</param>
        /// <param name="nodeid">Migration target node-id</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryPrepareSlotsForImport(HashSet<int> slots, string nodeid, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            while (true)
            {
                var current = currentConfig;
                var importingWorkerId = current.GetWorkerIdFromNodeId(nodeid);
                // Check importing nodeId is valid
                if (importingWorkerId == 0)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                    return false;
                }

                // Check local node is a primary
                if (current.GetLocalNodeRole() != NodeRole.PRIMARY)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR Importing node {current.GetLocalNodeRole()} is not a master node.");
                    return false;
                }

                // Check validity of slots
                foreach (var slot in slots)
                {
                    // Can only import remote slots
                    if (current.IsLocal((ushort)slot, readCommand: false))
                    {
                        errorMessage = Encoding.ASCII.GetBytes($"ERR This is a local hash slot {slot} and is already imported");
                        return false;
                    }

                    // Check if node is owned by node
                    var sourceNodeId = current.GetNodeIdFromSlot((ushort)slot);
                    if (sourceNodeId == null || !sourceNodeId.Equals(nodeid))
                    {
                        errorMessage = Encoding.ASCII.GetBytes($"ERR Slot {slot} is not owned by {nodeid}");
                        return false;
                    }

                    // Check if slot is in stable state
                    if (current.GetState((ushort)slot) != SlotState.STABLE)
                    {
                        errorMessage = Encoding.ASCII.GetBytes($"ERR Slot already scheduled for import from {nodeid}");
                        return false;
                    }
                }

                var newConfig = currentConfig.UpdateMultiSlotState(slots, importingWorkerId, SlotState.IMPORTING);
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }

            logger?.LogInformation("IMPORT {slot} FROM {importingNode}", string.Join(' ', slots), currentConfig.GetWorkerAddressFromNodeId(nodeid));
            return true;
        }

        /// <summary>
        /// Try to change ownership of slot to node.
        /// </summary>
        /// <param name="slot">Slot list</param>
        /// <param name="nodeid">Importing source node-id</param>
        /// <param name="errorMesage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryPrepareSlotForOwnershipChange(int slot, string nodeid, out ReadOnlySpan<byte> errorMesage)
        {
            errorMesage = default;
            var current = currentConfig;
            var workerId = current.GetWorkerIdFromNodeId(nodeid);
            if (workerId == 0)
            {
                errorMesage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                return false;
            }

            if (current.GetState((ushort)slot) is SlotState.MIGRATING)
            {
                while (true)
                {
                    current = currentConfig;
                    workerId = current.GetWorkerIdFromNodeId(nodeid);
                    var newConfig = currentConfig.UpdateSlotState(slot, workerId, SlotState.STABLE);

                    if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                        break;
                }
                FlushConfig();
                logger?.LogInformation("SLOT {slot} IMPORTED TO {nodeid}", slot, currentConfig.GetWorkerAddressFromNodeId(nodeid));
                return true;
            }
            else if (current.GetState((ushort)slot) is SlotState.IMPORTING)
            {
                if (!current.GetLocalNodeId().Equals(nodeid))
                {
                    errorMesage = Encoding.ASCII.GetBytes($"ERR Input nodeid {nodeid} different from local nodeid {CurrentConfig.GetLocalNodeId()}.");
                    return false;
                }

                while (true)
                {
                    current = currentConfig;
                    var newConfig = currentConfig.UpdateSlotState(slot, 1, SlotState.STABLE);
                    newConfig = newConfig.BumpLocalNodeConfigEpoch();

                    if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                        break;
                }
                FlushConfig();
                logger?.LogInformation("SLOT {slot} IMPORTED FROM {nodeid}", slot, currentConfig.GetWorkerAddressFromNodeId(nodeid));
                return true;
            }
            return true;
        }

        /// <summary>
        /// Try to change ownership of slots to node.
        /// </summary>
        /// <param name="slots">SLot list</param>
        /// <param name="nodeid">The id of the new owner node.</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryPrepareSlotsForOwnershipChange(HashSet<int> slots, string nodeid, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            while (true)
            {
                var current = currentConfig;
                var workerId = current.GetWorkerIdFromNodeId(nodeid);
                if (workerId == 0)
                {
                    errorMessage = Encoding.ASCII.GetBytes($"ERR I don't know about node {nodeid}");
                    return false;
                }

                var newConfig = currentConfig.UpdateMultiSlotState(slots, workerId, SlotState.STABLE);
                if (current.GetLocalNodeId().Equals(nodeid)) newConfig = newConfig.BumpLocalNodeConfigEpoch();
                if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                    break;
            }

            FlushConfig();
            logger?.LogInformation("SLOT {slot} IMPORTED TO {endpoint}", slots, currentConfig.GetWorkerAddressFromNodeId(nodeid));
            return true;
        }

        /// <summary>
        /// Try to reset slot state to <see cref="SlotState.STABLE"/>
        /// </summary>
        /// <param name="slot">Slot id to reset state</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryResetSlotState(int slot)
        {
            var current = currentConfig;
            var slotState = current.GetState((ushort)slot);
            if (slotState is SlotState.MIGRATING or SlotState.IMPORTING)
            {
                while (true)
                {
                    current = currentConfig;
                    slotState = current.GetState((ushort)slot);
                    var workerId = slotState == SlotState.MIGRATING ? 1 : currentConfig.GetWorkerIdFromSlot((ushort)slot);
                    var newConfig = currentConfig.UpdateSlotState(slot, workerId, SlotState.STABLE);
                    if (Interlocked.CompareExchange(ref currentConfig, newConfig, current) == current)
                        break;
                }
                FlushConfig();
            }
            return true;
        }

        /// <summary>
        /// Try to reset local slot state to <see cref="SlotState.STABLE"/>
        /// </summary>
        /// <param name="slots">Slot list</param>
        /// <param name="errorMessage">Error message</param>
        /// <returns>True on success, false otherwise</returns>
        public bool TryResetSlotsState(HashSet<int> slots, out ReadOnlySpan<byte> errorMessage)
        {
            errorMessage = default;
            foreach (var slot in slots)
            {
                if (!TryResetSlotState(slot))
                {
                    errorMessage = "ERR Failed to reset local slot state"u8;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Check if slot is in importing state.
        /// </summary>
        /// <param name="slot">Slot to check state</param>
        /// <returns>True if slot is in Importing state, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsImporting(ushort slot) => currentConfig.GetState(slot) == SlotState.IMPORTING;

        /// <summary>
        /// Methods used to cleanup keys for given slot collection in main store
        /// </summary>
        /// <param name="BasicGarnetApi"></param>
        /// <param name="slots">Slot list</param>
        public static unsafe void DeleteKeysInSlotsFromMainStore(BasicGarnetApi BasicGarnetApi, HashSet<int> slots)
        {
            using var iter = BasicGarnetApi.IterateMainStore();
            while (iter.GetNext(out var recordInfo))
            {
                ref SpanByte key = ref iter.GetKey();
                var s = NumUtils.HashSlot(key.ToPointer(), key.Length);
                if (slots.Contains(s))
                    _ = BasicGarnetApi.DELETE(ref key, StoreType.Main);
            }
        }

        /// <summary>
        /// Methods used to cleanup keys for given slot collection in object store
        /// </summary>
        /// <param name="BasicGarnetApi"></param>
        /// <param name="slots">Slot list</param>
        public static unsafe void DeleteKeysInSlotsFromObjectStore(BasicGarnetApi BasicGarnetApi, HashSet<int> slots)
        {
            using var iterObject = BasicGarnetApi.IterateObjectStore();
            while (iterObject.GetNext(out var recordInfo))
            {
                ref var key = ref iterObject.GetKey();
                ref var value = ref iterObject.GetValue();
                var s = NumUtils.HashSlot(key);
                if (slots.Contains(s))
                    _ = BasicGarnetApi.DELETE(key, StoreType.Object);
            }
        }
    }
}