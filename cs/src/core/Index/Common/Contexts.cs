﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FASTER.core
{
    internal enum OperationType
    {
        READ,
        RMW,
        UPSERT,
        INSERT,
        DELETE
    }

    internal enum OperationStatus
    {
        SUCCESS,
        NOTFOUND,
        RETRY_NOW,
        RETRY_LATER,
        RECORD_ON_DISK,
        SUCCESS_UNMARK,
        CPR_SHIFT_DETECTED,
        CPR_PENDING_DETECTED
    }

    internal unsafe struct AsyncIOContext
    {
        public long id;

        public Key* key;

        public long logicalAddress;
        
        public SectorAlignedMemory record;

        public SectorAlignedMemory objBuffer;

        public BlockingCollection<AsyncIOContext> callbackQueue;
    }

    internal unsafe struct PendingContext
    {
        // User provided information

        public OperationType type;

        public Key* key;

        public Input* input;

        public Output* output;

        public Value* value;

        public Context* userContext;

        // Some additional information about the previous attempt

        public long id;

        public int version;

        public long logicalAddress;

        public long serialNum;

        public HashBucketEntry entry;
    }

    internal unsafe class ExecutionContext
    {
        public int version;
        public long serialNum;
        public Phase phase;
        public bool[] markers;
        public long totalPending;
        public Guid guid;
        public Queue<PendingContext> retryRequests;
        public Dictionary<long, PendingContext> ioPendingRequests;
        public BlockingCollection<AsyncIOContext> readyResponses;

        public void Write(StreamWriter writer)
        {
            writer.WriteLine(version);
            writer.WriteLine(guid);
            writer.WriteLine(serialNum);
        }
        public void Load(StreamReader reader)
        {
            string value = reader.ReadLine();
            version = int.Parse(value);

            value = reader.ReadLine();
            guid = Guid.Parse(value);

            value = reader.ReadLine();
            serialNum = long.Parse(value);
        }
    }

    internal struct DirectoryConfiguration
    {
        public const string index_base_folder = "index-checkpoints";
        public const string index_meta_file = "info";
        public const string hash_table_file = "ht";
        public const string overflow_buckets_file = "ofb";
        public const string snapshot_file = "snapshot";

        public const string cpr_base_folder = "cpr-checkpoints";
        public const string cpr_meta_file = "info";

        public const string hlog_file = "lss.log";

        public static void CreateIndexCheckpointFolder(Guid token)
        {
            var directory = GetIndexCheckpointFolder(token);
            Directory.CreateDirectory(directory);
            DirectoryInfo directoryInfo = new System.IO.DirectoryInfo(directory);
            foreach (System.IO.FileInfo file in directoryInfo.GetFiles())
                file.Delete();
        }
        public static void CreateHybridLogCheckpointFolder(Guid token)
        {
            var directory = GetHybridLogCheckpointFolder(token);
            Directory.CreateDirectory(directory);
            DirectoryInfo directoryInfo = new System.IO.DirectoryInfo(directory);
            foreach (System.IO.FileInfo file in directoryInfo.GetFiles())
                file.Delete();
        }

        public static string GetIndexCheckpointFolder(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}", Config.CheckpointDirectory, index_base_folder, token);
        }
        public static string GetHybridLogCheckpointFolder(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}", Config.CheckpointDirectory, cpr_base_folder, token);
        }
        public static string GetIndexCheckpointMetaFileName(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    index_base_folder,
                                    token,
                                    index_meta_file);
        }
        public static string GetPrimaryHashTableFileName(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    index_base_folder,
                                    token,
                                    hash_table_file);
        }
        public static string GetOverflowBucketsFileName(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    index_base_folder,
                                    token,
                                    overflow_buckets_file);
        }
        public static string GetHybridLogCheckpointMetaFileName(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    cpr_base_folder,
                                    token,
                                    cpr_meta_file);
        }
        public static string GetHybridLogCheckpointContextFileName(Guid checkpointToken, Guid sessionToken)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    cpr_base_folder,
                                    checkpointToken,
                                    sessionToken);
        }
        public static string GetHybridLogCheckpointFileName(Guid token)
        {
            return String.Format("{0}\\{1}\\{2}\\{3}.dat",
                                    Config.CheckpointDirectory,
                                    cpr_base_folder,
                                    token,
                                    snapshot_file);
        }
    }

    /// <summary>
    /// Recovery info for hybrid log
    /// </summary>
    public struct HybridLogRecoveryInfo
    {
        /// <summary>
        /// Guid
        /// </summary>
        public Guid guid;
        /// <summary>
        /// Use snapshot file
        /// </summary>
        public int useSnapshotFile;
        /// <summary>
        /// Version
        /// </summary>
        public int version;
        /// <summary>
        /// Number of threads
        /// </summary>
        public int numThreads;
        /// <summary>
        /// Flushed logical address
        /// </summary>
        public long flushedLogicalAddress;
        /// <summary>
        /// Start logical address
        /// </summary>
        public long startLogicalAddress;
        /// <summary>
        /// Final logical address
        /// </summary>
        public long finalLogicalAddress;
        /// <summary>
        /// Guid array
        /// </summary>
        public Guid[] guids;
        /// <summary>
        /// Tokens per guid
        /// </summary>
        public Dictionary<Guid, long> continueTokens;

        /// <summary>
        /// Initialize
        /// </summary>
        /// <param name="token"></param>
        /// <param name="_version"></param>
        public void Initialize(Guid token, int _version)
        {
            guid = token;
            useSnapshotFile = 0;
            version = _version;
            numThreads = 0;
            flushedLogicalAddress = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
            guids = new Guid[LightEpoch.kTableSize+1];
            continueTokens = new Dictionary<Guid, long>();
        }

        /// <summary>
        /// Initialize from stream
        /// </summary>
        /// <param name="reader"></param>
        public void Initialize(StreamReader reader)
        {
            guids = new Guid[64];
            continueTokens = new Dictionary<Guid, long>();

            string value = reader.ReadLine();
            guid = Guid.Parse(value);

            value = reader.ReadLine();
            useSnapshotFile = int.Parse(value);

            value = reader.ReadLine();
            version = int.Parse(value);

            value = reader.ReadLine();
            flushedLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            startLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            finalLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            numThreads = int.Parse(value);

            for (int i = 0; i < numThreads; i++)
            {
                value = reader.ReadLine();
                guids[i] = Guid.Parse(value);
            }
        }

        /// <summary>
        /// Recover info from token and checkpoint directory
        /// </summary>
        /// <param name="token"></param>
        /// <param name="checkpointDir"></param>
        /// <returns></returns>
        public bool Recover(Guid token, string checkpointDir)
        {
            Config.CheckpointDirectory = checkpointDir;
            return Recover(token);
        }

        /// <summary>
        ///  Recover info from token
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        internal bool Recover(Guid token)
        {
            string checkpointInfoFile = DirectoryConfiguration.GetHybridLogCheckpointMetaFileName(token);
            using (var reader = new StreamReader(checkpointInfoFile))
            {
                Initialize(reader);
            }

            int num_threads = numThreads;
            for (int i = 0; i < num_threads; i++)
            {
                var guid = guids[i];
                using (var reader = new StreamReader(DirectoryConfiguration.GetHybridLogCheckpointContextFileName(token, guid)))
                {
                    var ctx = new ExecutionContext();
                    ctx.Load(reader);
                    continueTokens.Add(ctx.guid, ctx.serialNum);
                }
            }

            if(continueTokens.Count == num_threads)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Reset
        /// </summary>
        public void Reset()
        {
            Initialize(default(Guid), -1);
        }

        /// <summary>
        /// Write info to file
        /// </summary>
        /// <param name="writer"></param>
        public void Write(StreamWriter writer)
        {
            writer.WriteLine(guid);
            writer.WriteLine(useSnapshotFile);
            writer.WriteLine(version);
            writer.WriteLine(flushedLogicalAddress);
            writer.WriteLine(startLogicalAddress);
            writer.WriteLine(finalLogicalAddress);
            writer.WriteLine(numThreads);
            for(int i = 0; i < numThreads; i++)
            {
                writer.WriteLine(guids[i]);
            }
        }
    }

    internal struct HybridLogCheckpointInfo
    {
        public HybridLogRecoveryInfo info;
        public IDevice snapshotFileDevice;
        public IDevice snapshotFileObjectLogDevice;
        public CountdownEvent flushed;
        public long started;

        public void Initialize(Guid token, int _version)
        {
            info.Initialize(token, _version);
            started = 0;
        }
        public void Recover(Guid token)
        {
            info.Recover(token);
            started = 0;
        }
        public void Reset()
        {
            started = 0;
            flushed = null;
            info.Reset();
            if (snapshotFileDevice != null) snapshotFileDevice.Close();
            if (snapshotFileObjectLogDevice != null) snapshotFileObjectLogDevice.Close();
        }
    }

    internal struct IndexRecoveryInfo
    {
        public Guid token;
        public long table_size;
        public ulong num_ht_bytes;
        public ulong num_ofb_bytes;
        public int num_buckets;
        public long startLogicalAddress;
        public long finalLogicalAddress;

        public void Initialize(Guid token, long _size)
        {
            this.token = token;
            table_size = _size;
            num_ht_bytes = 0;
            num_ofb_bytes = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
            num_buckets = 0;
        }
        public void Initialize(StreamReader reader)
        {
            string value = reader.ReadLine();
            token = Guid.Parse(value);

            value = reader.ReadLine();
            table_size = long.Parse(value);

            value = reader.ReadLine();
            num_ht_bytes = ulong.Parse(value);

            value = reader.ReadLine();
            num_ofb_bytes = ulong.Parse(value);

            value = reader.ReadLine();
            num_buckets = int.Parse(value);

            value = reader.ReadLine();
            startLogicalAddress = long.Parse(value);

            value = reader.ReadLine();
            finalLogicalAddress = long.Parse(value);
        }
        public void Recover(Guid guid)
        {
            string indexInfoFile = DirectoryConfiguration.GetIndexCheckpointMetaFileName(guid);
            using (var reader = new StreamReader(indexInfoFile))
            {
                Initialize(reader);
            }
        }
        public void Write(StreamWriter writer)
        {
            writer.WriteLine(token);
            writer.WriteLine(table_size);
            writer.WriteLine(num_ht_bytes);
            writer.WriteLine(num_ofb_bytes);
            writer.WriteLine(num_buckets);
            writer.WriteLine(startLogicalAddress);
            writer.WriteLine(finalLogicalAddress);
        }
        public void Reset()
        {
            token = default(Guid);
            table_size = 0;
            num_ht_bytes = 0;
            num_ofb_bytes = 0;
            num_buckets = 0;
            startLogicalAddress = 0;
            finalLogicalAddress = 0;
        }
    }

    internal struct IndexCheckpointInfo
    {
        public IndexRecoveryInfo info;
        public IDevice main_ht_device;
        public IDevice ofb_device;

        public void Initialize(Guid token, long _size)
        {
            info.Initialize(token, _size);
            main_ht_device = new LocalStorageDevice(DirectoryConfiguration.GetPrimaryHashTableFileName(token));
            ofb_device = new LocalStorageDevice(DirectoryConfiguration.GetOverflowBucketsFileName(token));
        }
        public void Recover(Guid token)
        {
            info.Recover(token);
        }
        public void Reset()
        {
            info.Reset();
            main_ht_device.Close();
            ofb_device.Close();
        }
    }
}
