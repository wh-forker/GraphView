﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Newtonsoft.Json.Linq;

namespace GraphView.Transaction
{
    internal class VersionEntry
    {
        private bool isBeginTxId;
        private long beginTimestamp;
        private bool isEndTxId;
        private long endTimestamp;
        private object record;

        public bool IsBeginTxId
        {
            get
            {
                return this.isBeginTxId;
            }
            set
            {
                this.isBeginTxId = value;
            }
        }

        public long BeginTimestamp
        {
            get
            {
                return this.beginTimestamp;
            }
            set
            {
                this.beginTimestamp = value;
            }
        }

        public bool IsEndTxId
        {
            get
            {
                return this.isEndTxId;
            }
            set
            {
                this.isEndTxId = value;
            }
        }

        public long EndTimestamp
        {
            get
            {
                return this.endTimestamp;
            }
            set
            {
                this.endTimestamp = value;
            }
        }

        public JObject Record
        {
            get
            {
                return (JObject) this.record;
            }
            set
            {
                this.record = value;
            }
        }

        public VersionEntry(bool isBeginTxId, long beginTimestamp, bool isEndTxId, long endTimestamp, JObject jObject)
        {
            this.isBeginTxId = isBeginTxId;
            this.beginTimestamp = beginTimestamp;
            this.isEndTxId = isEndTxId;
            this.endTimestamp = endTimestamp;
            this.record = jObject;
        }
    }

    /// <summary>
    /// A version Db for concurrency control.
    /// </summary>
    public abstract class VersionDb
    {
        internal virtual VersionEntry ReadVersion(
            string tableId, 
            object recordKey, 
            long readTimestamp)
        {
            throw new NotImplementedException();
        }

        internal virtual bool InsertVersion(
            string tableId, 
            object recordKey, 
            JObject record, 
            long txId, 
            long readTimestamp)
        {
            throw new NotImplementedException();
        }

        internal virtual bool DeleteVersion(
            string tableId,
            object recordKey,
            long txId,
            long readTimestamp,
            out VersionEntry deletedVersion)
        {
            throw new NotImplementedException();
        }

        internal virtual bool UpdateVersion(
            string tableId,
            object recordKey,
            JObject record,
            long txId,
            long readTimestamp,
            out VersionEntry oldVersion,
            out VersionEntry newVersion)
        {
            throw new NotImplementedException();
        }

        internal virtual bool CheckReadVisibility(
            string tableId,
            object recordKey,
            long readVersionBeginTimestamp,
            long readTimestamp,
            long txId)
        {
            throw new NotImplementedException();
        }

        internal virtual bool CheckPhantom(
            string tableId,
            object recordKey,
            long oldScanTime, 
            long newScanTime)
        {
            throw new NotImplementedException();
        }

        internal virtual void UpdateCommittedVersionTimestamp(
            string tableId,
            object recordKey,
            long txId,
            long endTimestamp)
        {
            throw new NotImplementedException();
        }

        internal virtual void UpdateAbortedVersionTimestamp(
            string tableId,
            object recordKey,
            long txId)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// A version table for concurrency control.
    /// </summary>
    public abstract class VersionTable
    {
        private string tableId;

        public string TableId
        {
            get
            {
                return this.tableId;
            }
            set
            {
                this.tableId = value;
            }
        }

        internal virtual IList<VersionEntry> GetVersionList(object recordKey)
        {
            throw new NotImplementedException();
        }

        internal virtual void InsertAndUploadVersion(object recordKey, VersionEntry version)
        {
            throw new NotImplementedException();
        }

        internal virtual bool UpdateAndUploadVersion(object recordKey, VersionEntry oldVersion, VersionEntry newVersion)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Given a version and a read timestamp (or a TxId), check this version's visibility.
        /// </summary>
        internal bool CheckVersionVisibility(VersionEntry version, long readTimestamp)
        {
            //case 1: both begin and end fields are timestamp
            //just checl whether readTimestamp is in the interval of the version's beginTimestamp and endTimestamp 
            if (!version.IsBeginTxId && !version.IsEndTxId)
            {
                return readTimestamp > version.BeginTimestamp && readTimestamp < version.EndTimestamp;
            }
            //case 2: begin field is a TxId, end field is a timestamp
            //just check whether this version is created by the same transaction
            else if (version.IsBeginTxId && !version.IsEndTxId)
            {
                return version.BeginTimestamp == readTimestamp;
            }
            //case 3: begin field is a TxId, end field is a TxId
            //this must must be deleted by the same transaction, not visible
            else if (version.IsBeginTxId && version.IsEndTxId)
            {
                return false;
            }
            //case 4: begin field is a timestamp, end field is a TxId
            //first check whether the readTimestamp > version's beginTimestamp
            //then, check the version's end field
            else
            {
                if (readTimestamp > version.BeginTimestamp)
                {
                    //this is the old version deleted by the same Transaction
                    if (version.EndTimestamp == readTimestamp)
                    {
                        return false;
                    }
                    //other transaction can see this old version
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Given the recordKey and the readTimestamp, this method will first scan a list to check each version's visibility.
        /// If it finds the legal version, it will return the version, or, it will return null.
        /// </summary>
        internal VersionEntry ReadVersion(object recordKey, long readTimestamp)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList == null)
            {
                return null;
            }

            foreach (VersionEntry version in versionList)
            {
                if (this.CheckVersionVisibility(version, readTimestamp))
                {
                    return version;
                }
            }

            return null;
        }

        /// <summary>
        /// Given the recordKey, the record to insert, the transactionId, and the readTimestamp,
        /// this method will first tranverse the version list to check each version's visibility.
        /// If no version is visible, create and insert a new version to the dictionary and return true, or, it will return false.
        /// </summary>
        internal bool InsertVersion(object recordKey, JObject record, long txId, long readTimestamp)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList != null)
            {
                foreach (VersionEntry version in versionList)
                {
                    if (this.CheckVersionVisibility(version, readTimestamp))
                    {
                        return false;
                    }
                }
            }

            this.InsertAndUploadVersion(recordKey, new VersionEntry(true, txId, false, long.MaxValue, record));
            return true;
        }

        /// <summary>
        /// Update a version.
        /// Find the version, atomically change it to old version, and insert a new version.
        /// </summary>
        internal bool UpdateVersion(
            object recordKey, 
            JObject record, 
            long txId, 
            long readTimestamp, 
            out VersionEntry oldVersion, 
            out VersionEntry newVersion)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList == null)
            {
                oldVersion = null;
                newVersion = null;
                return false;
            }

            foreach (VersionEntry version in versionList)
            {
                //first check the version's visibility
                if (!this.CheckVersionVisibility(version, readTimestamp))
                {
                    continue;
                }
                //check the version's updatability
                if (!version.IsEndTxId && version.EndTimestamp == long.MaxValue)
                {
                    //updatable, two case:
                    //case 1: if the version's begin field is a timestamp, 
                    if (!version.IsBeginTxId)
                    {
                        //(1) ATOMICALLY set the version's end timestamp to TxId,
                        //if (1) failed, other transaction has already set the version's end field, can not update.
                        //if (1) success, insert a new version
                        if (this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(version.IsBeginTxId, version.BeginTimestamp, true, txId, version.Record)))
                        {
                            //(1) success
                            newVersion = new VersionEntry(true, txId, false, long.MaxValue, record);
                            this.InsertAndUploadVersion(recordKey, newVersion);
                            oldVersion = version;
                            return true;
                        }
                        //(1) failed
                        oldVersion = version;
                        newVersion = null;
                        return false;
                    }
                    //case 2: if the version's begin field is a TxId
                    else
                    {
                        //change the record directly on this version
                        this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(version.IsBeginTxId, version.BeginTimestamp, version.IsEndTxId, version.EndTimestamp, record));
                        oldVersion = null;
                        version.Record = record;
                        newVersion = version;
                        return true;
                    }
                }
                //a version is visible but not updatable, can not perform update, return false
                else
                {
                    oldVersion = null;
                    newVersion = null;
                    return false;
                }
            }
            //can not find the legal version to perform update
            oldVersion = null;
            newVersion = null;
            return false;
        }

        /// <summary>
        /// Find and delete a version.
        /// </summary>
        internal bool DeleteVersion(
            object recordKey,
            long txId,
            long readTimestamp,
            out VersionEntry deletedVersion)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList == null)
            {
                deletedVersion = null;
                return false;
            }

            //tranverse the version list, try to find the deletable version
            foreach (VersionEntry version in versionList)
            {
                //first check the version's visibility
                if (!this.CheckVersionVisibility(version, readTimestamp))
                {
                    continue;
                }
                //check the version's updatability
                if (!version.IsEndTxId && version.EndTimestamp == long.MaxValue)
                {
                    //deletable, two case:
                    //case 1: if the version's begin field is a timestamp, 
                    if (!version.IsBeginTxId)
                    {
                        //if the versiin's begin field is a timestamp
                        //(1) ATOMICALLY set the version's end timestamp to TxId
                        //if (1) failed, other transaction has already set the version's end field, can not delete
                        if (this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(version.IsBeginTxId, version.BeginTimestamp, true, txId, version.Record)))
                        {
                            //success
                            deletedVersion = version;
                            return true;
                        }
                        //failed
                        deletedVersion = version;
                        return false;
                    }
                    //case 2: if the version's begin field is a TxId, set the version's end timestamp to TxId directly
                    else
                    {
                        this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(version.IsBeginTxId, version.BeginTimestamp, true, txId, version.Record));
                        deletedVersion = version;
                        return true;
                    }
                }
                //a version is visible but not deletable, can not perform delete, return false
                else
                {
                    deletedVersion = null;
                    return false;
                }
            }
            //can not find the legal version to perform delete
            deletedVersion = null;
            return false;
        }

        /// <summary>
        /// Check visibility of the version read before, used in validation phase.
        /// Given a record's recordKey, the version's beginTimestamp, the current readTimestamp, and the transaction Id
        /// First find the version then check whether it is stil visible. 
        /// </summary>
        internal bool CheckReadVisibility(
            object recordKey, 
            long readVersionBeginTimestamp, 
            long readTimestamp,
            long txId)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList == null)
            {
                return false;
            }

            foreach (VersionEntry version in versionList)
            {
                //case 1: the versin's begin field is a TxId, and this Id equals to the transaction's Id
                if (version.IsBeginTxId && version.BeginTimestamp == txId)
                {
                    //check the visibility of this version, using the transaction's endTimestamp
                    if (this.CheckVersionVisibility(version, readTimestamp))
                    {
                        return true;
                    }
                    else
                    {
                        continue;
                    }
                }
                //case 2: the version's begin field is a timestamp, and this timestamp equals to the read version's begin timestamp
                else if (!version.IsBeginTxId && version.BeginTimestamp == readVersionBeginTimestamp)
                {
                    return this.CheckVersionVisibility(version, readTimestamp);
                }
            }

            return false;
        }

        /// <summary>
        /// Check for Phantom of a scan.
        /// Only check for version phantom currently. Check key phantom is NOT implemented.
        /// Look for versions that came into existence during T’s lifetime and are visible as of the end of the transaction.
        /// </summary>
        internal bool CheckPhantom(object recordKey, long oldScanTime, long newScanTime)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList == null)
            {
                return true;
            }

            foreach (VersionEntry version in versionList)
            {
                if (!version.IsBeginTxId)
                {
                    if (version.BeginTimestamp > oldScanTime && version.BeginTimestamp < newScanTime)
                    {
                        if (this.CheckVersionVisibility(version, newScanTime))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// After a transaction T commit,
        /// propagate a T's end timestamp to the Begin and End fields of new and old versions
        /// </summary>
        internal void UpdateCommittedVersionTimestamp(
            object recordKey, 
            long txId, 
            long endTimestamp)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList != null)
            {
                //tranverse the whole version list and seach for versions that were modified by the transaction
                foreach (VersionEntry version in versionList)
                {
                    if (version.IsBeginTxId && version.BeginTimestamp == txId ||
                        version.IsEndTxId && version.EndTimestamp == txId)
                    {
                        VersionEntry commitedVersion = version;
                        if (version.IsBeginTxId)
                        {
                            commitedVersion.IsBeginTxId = false;
                            commitedVersion.BeginTimestamp = endTimestamp;
                        }

                        if (version.IsEndTxId)
                        {
                            commitedVersion.IsEndTxId = false;
                            commitedVersion.EndTimestamp = endTimestamp;
                        }

                        this.UpdateAndUploadVersion(recordKey, version, commitedVersion);
                    }
                }
            }
        }

        /// <summary>
        /// After a transaction T abort,
        /// T sets the Begin field of its new versions to infinity, thereby making them invisible to all transactions,
        /// and reset the End fields of its old versions to infinity.
        /// </summary>
        internal void UpdateAbortedVersionTimestamp(object recordKey, long txId)
        {
            IList<VersionEntry> versionList = this.GetVersionList(recordKey);

            if (versionList != null)
            {
                //tranverse the whole version list and seach for versions that were modified by the transaction
                foreach (VersionEntry version in versionList)
                {
                    //new version
                    if (version.IsBeginTxId && version.BeginTimestamp == txId)
                    {
                        this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(false, long.MaxValue, version.IsEndTxId, version.EndTimestamp,
                                version.Record));
                    }
                    //old version
                    else if (version.IsEndTxId && version.EndTimestamp == txId)
                    {
                        this.UpdateAndUploadVersion(recordKey, version,
                            new VersionEntry(version.IsBeginTxId, version.BeginTimestamp, false, long.MaxValue,
                                version.Record));
                    }
                }
            }
        }
    }

}