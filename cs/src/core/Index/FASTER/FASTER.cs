﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

#pragma warning disable 0162

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FASTER.core
{
    public unsafe partial class FasterKV : FasterBase, IFasterKV
    { 
        private PersistentMemoryMalloc hlog;

        private static int numPendingReads = 0;

        private const bool kCopyReadsToTail = false;
        private const bool breakWhenClassIsLoaded = false;

        /// <summary>
        /// Tail address of log
        /// </summary>
        public long LogTailAddress => hlog.GetTailAddress();

        /// <summary>
        /// Read-only address of log
        /// </summary>
        public long LogReadOnlyAddress => hlog.SafeReadOnlyAddress;

        /// <summary>
        /// Number of used entries in hash index
        /// </summary>
        public long EntryCount => GetEntryCount();

        private enum CheckpointType
        {
            INDEX_ONLY,
            HYBRID_LOG_ONLY,
            FULL,
            NONE
        }

        private CheckpointType _checkpointType;
        private Guid _indexCheckpointToken;
        private Guid _hybridLogCheckpointToken;
        private SystemState _systemState;

        private HybridLogCheckpointInfo _hybridLogCheckpoint;

        [ThreadStatic]
        private static ExecutionContext prevThreadCtx = default(ExecutionContext);

        [ThreadStatic]
        private static ExecutionContext threadCtx = default(ExecutionContext);


        static FasterKV()
        {
            if (breakWhenClassIsLoaded)
            {
                if (System.Diagnostics.Debugger.IsAttached)
                    System.Diagnostics.Debugger.Break();
                else
                    System.Diagnostics.Debugger.Launch();
            }
        }
        
        /// <summary>
        /// Create FASTER instance
        /// </summary>
        /// <param name="size"></param>
        /// <param name="logDevice"></param>
        /// <param name="objectLogDevice"></param>
        /// <param name="checkpointDir"></param>
        public FasterKV(long size, IDevice logDevice, IDevice objectLogDevice, string checkpointDir = null)
        {
            if (checkpointDir != null)
                Config.CheckpointDirectory = checkpointDir;

            hlog = new PersistentMemoryMalloc(logDevice, objectLogDevice);
            var recordSize = Layout.EstimatePhysicalSize(null, null);
            Initialize(size, hlog.GetSectorSize());

            _systemState = default(SystemState);
            _systemState.phase = Phase.REST;
            _systemState.version = 1;
            _checkpointType = CheckpointType.HYBRID_LOG_ONLY;
        }


        /// <summary>
        /// Take full checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeFullCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.FULL);
            if(success)
            {
                token = _indexCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Take index checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeIndexCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.INDEX_ONLY);
            if (success)
            {
                token = _indexCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Take hybrid log checkpoint
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool TakeHybridLogCheckpoint(out Guid token)
        {
            var success = InternalTakeCheckpoint(CheckpointType.HYBRID_LOG_ONLY);
            if (success)
            {
                token = _hybridLogCheckpointToken;
            }
            else
            {
                token = default(Guid);
            }
            return success;
        }

        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="fullCheckpointToken"></param>
        public void Recover(Guid fullCheckpointToken)
        {
            InternalRecover(fullCheckpointToken, fullCheckpointToken);
        } 

        /// <summary>
        /// Recover
        /// </summary>
        /// <param name="indexCheckpointToken"></param>
        /// <param name="hybridLogCheckpointToken"></param>
        public void Recover(Guid indexCheckpointToken, Guid hybridLogCheckpointToken)
        {
            InternalRecover(indexCheckpointToken, hybridLogCheckpointToken);
        }

        /// <summary>
        /// Start session with FASTER
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Guid StartSession()
        {
            return InternalAcquire();
        }


        /// <summary>
        /// Continue session with FASTER
        /// </summary>
        /// <param name="guid"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ContinueSession(Guid guid)
        {
            return InternalContinue(guid);
        }

        /// <summary>
        /// Stop session with FASTER
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StopSession()
        {
            InternalRelease();
        }

        /// <summary>
        /// Refresh epoch (release memory pins)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Refresh()
        {
            InternalRefresh();
        }

        
        /// <summary>
        /// Complete outstanding pending operations
        /// </summary>
        /// <param name="wait"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CompletePending(bool wait = false)
        {
            return InternalCompletePending(wait);
        }

        /// <summary>
        /// Complete the ongoing checkpoint (if any)
        /// </summary>
        /// <param name="wait"></param>
        /// <returns></returns>
        public bool CompleteCheckpoint(bool wait = false)
        {
            do
            {
                CompletePending();
                if (_systemState.phase == Phase.REST)
                {
                    CompletePending();
                    return true;
                }
            } while (wait);
            return false;
        }

        /// <summary>
        /// Read
        /// </summary>
        /// <param name="key"></param>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="userContext"></param>
        /// <param name="monotonicSerialNum"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Read(Key* key, Input* input, Output* output, Context* userContext, long monotonicSerialNum)
        {
            var context = default(PendingContext);
            var internalStatus = InternalRead(key, input, output, userContext, ref context);
            var status = default(Status);
            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                
                status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(threadCtx, context, internalStatus);
            }
            threadCtx.serialNum = monotonicSerialNum;
            return status;
        }

        /// <summary>
        /// Upsert
        /// </summary>
        /// <param name="key"></param>
        /// <param name="desiredValue"></param>
        /// <param name="userContext"></param>
        /// <param name="monotonicSerialNum"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status Upsert(Key* key, Value* desiredValue, Context* userContext, long monotonicSerialNum)
        {
            var context = default(PendingContext);
            var internalStatus = InternalUpsert(key, desiredValue, userContext, ref context);
            var status = default(Status);

            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(threadCtx, context, internalStatus);
            }
            threadCtx.serialNum = monotonicSerialNum;
            return status;
        }

        /// <summary>
        /// Read-modify-write
        /// </summary>
        /// <param name="key"></param>
        /// <param name="input"></param>
        /// <param name="userContext"></param>
        /// <param name="monotonicSerialNum"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Status RMW(Key* key, Input* input, Context* userContext, long monotonicSerialNum)
        {
            var context = default(PendingContext);
            var internalStatus = InternalRMW(key, input, userContext, ref context);
            var status = default(Status);
            if (internalStatus == OperationStatus.SUCCESS || internalStatus == OperationStatus.NOTFOUND)
            {
                 status = (Status)internalStatus;
            }
            else
            {
                status = HandleOperationStatus(threadCtx, context, internalStatus);
            }
            threadCtx.serialNum = monotonicSerialNum;
            return status;
        }

        /// <summary>
        /// Truncate the log until, but not including, untilAddress
        /// </summary>
        /// <param name="untilAddress"></param>
        public bool ShiftBeginAddress(long untilAddress)
        {
            return InternalShiftBeginAddress(untilAddress);
        }

        /// <summary>
        /// Grow the hash index
        /// </summary>
        /// <returns></returns>
        public bool GrowIndex()
        {
            return InternalGrowIndex();
        }
        
        /// <summary>
        /// Dispose FASTER instance
        /// </summary>
        public void Dispose()
        {
            hlog.Dispose();
        }
    }
}
