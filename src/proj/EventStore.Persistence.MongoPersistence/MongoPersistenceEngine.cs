﻿namespace EventStore.Persistence.MongoPersistence
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using MongoDB.Bson;
	using MongoDB.Driver;
	using MongoDB.Driver.Builders;
	using Serialization;

	public class MongoPersistenceEngine : IPersistStreams
	{
		private const string ConcurrencyException = "E1100";
		private readonly MongoDatabase store;
		private readonly ISerialize serializer;
		private bool disposed;

		public MongoPersistenceEngine(MongoDatabase store, ISerialize serializer)
		{
			this.store = store;
			this.serializer = serializer;
		}

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}
		protected virtual void Dispose(bool disposing)
		{
			if (!disposing || this.disposed)
				return;

			this.disposed = true;
		}

		public virtual void Initialize()
		{
			this.PersistedCommits.EnsureIndex(
				IndexKeys.Ascending("Dispatched").Ascending("CommitStamp"),
				IndexOptions.SetName("Dispatched_Index").SetUnique(false));

			this.PersistedCommits.EnsureIndex(
				IndexKeys.Ascending("_id.StreamId", "StartingStreamRevision", "StreamRevision"),
				IndexOptions.SetName("GetFrom_Index").SetUnique(true));

			this.PersistedCommits.EnsureIndex(
				IndexKeys.Ascending("CommitStamp"),
				IndexOptions.SetName("CommitStamp_Index").SetUnique(false));
		}

		public virtual IEnumerable<Commit> GetFrom(Guid streamId, int minRevision, int maxRevision)
		{
			try
			{
				var query = Query.And(
					Query.EQ("_id.StreamId", streamId),
					Query.GTE("StreamRevision", minRevision),
					Query.LTE("StartingStreamRevision", maxRevision));

				return this.PersistedCommits
					.Find(query)
					.SetSortOrder("StartingStreamRevision")
					.Select(mc => mc.ToCommit(this.serializer));
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}
		public virtual IEnumerable<Commit> GetFrom(DateTime start)
		{
			try
			{
				var query = Query.GTE("CommitStamp", start);

				return this.PersistedCommits
					.Find(query)
					.SetSortOrder("CommitStamp")
					.Select(x => x.ToCommit(this.serializer));
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		public virtual void Commit(Commit attempt)
		{
			var commit = attempt.ToMongoCommit(this.serializer);

			try
			{
				// for concurrency / duplicate commit detection safe mode is required
				this.PersistedCommits.Insert(commit, SafeMode.True);
				this.UpdateStreamHeadAsync(commit.Id.StreamId, commit.StreamRevision, (commit.Id.CommitSequence == 1));
			}
			catch (MongoException e)
			{
				if (!e.Message.Contains(ConcurrencyException))
					throw new StorageException(e.Message, e);

				var committed = this.PersistedCommits.FindOne(commit.ToMongoCommitIdQuery());
				if (committed == null || committed.CommitId == commit.CommitId)
					throw new DuplicateCommitException();

				throw new ConcurrencyException();
			}
		}

		public virtual IEnumerable<Commit> GetUndispatchedCommits()
		{
			var query = Query.EQ("Dispatched", false);

			return this.PersistedCommits
				.Find(query)
				.SetSortOrder("CommitStamp")
				.Select(mc => mc.ToCommit(this.serializer));
		}
		public virtual void MarkCommitAsDispatched(Commit commit)
		{
			var query = commit.ToMongoCommitIdQuery();
			var update = Update.Set("Dispatched", true);
			this.PersistedCommits.Update(query, update);
		}

		public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
		{
			var query = Query
				.Where(BsonJavaScript.Create("this.HeadRevision >= this.SnapshotRevision + " + maxThreshold));

			return this.PersistedStreamHeads
				.Find(query)
				.ToArray()
				.Select(x => x.ToStreamHead());
		}
		public virtual Snapshot GetSnapshot(Guid streamId, int maxRevision)
		{
			return this.PersistedSnapshots
				.FindAs<BsonDocument>(streamId.ToSnapshotQuery(maxRevision))
				.SetSortOrder(SortBy.Descending("_id"))
				.SetLimit(1)
				.Select(mc => mc.ToSnapshot(this.serializer))
				.FirstOrDefault();
		}

		public virtual bool AddSnapshot(Snapshot snapshot)
		{
			if (snapshot == null)
				return false;

			try
			{
				var mongoSnapshot = snapshot.ToMongoSnapshot(this.serializer);
				this.PersistedSnapshots.Insert(mongoSnapshot);
				this.PersistedStreamHeads.Update(
					Query.EQ("_id", snapshot.StreamId),
					Update.Set("SnapshotRevision", snapshot.StreamRevision));

				return true;
			}
			catch (MongoException)
			{
				return false;
			}
		}

		private void UpdateStreamHeadAsync(Guid streamId, int streamRevision, bool isFirstCommit)
		{
			ThreadPool.QueueUserWorkItem(x =>
			{
				if (isFirstCommit)
					this.PersistedStreamHeads.Insert(
						new MongoStreamHead(streamId, streamRevision, 0),
						SafeMode.False);
				else
					this.PersistedStreamHeads.Update(
						Query.EQ("_id", streamId),
						Update.Set("HeadRevision", streamRevision),
						SafeMode.False);
			}, null);
		}

		protected virtual MongoCollection<MongoCommit> PersistedCommits
		{
			get { return this.store.GetCollection<MongoCommit>("Commits"); }
		}
		protected virtual MongoCollection<MongoSnapshot> PersistedSnapshots
		{
			get { return this.store.GetCollection<MongoSnapshot>("Snapshots"); }
		}
		protected virtual MongoCollection<MongoStreamHead> PersistedStreamHeads
		{
			get { return this.store.GetCollection<MongoStreamHead>("Streams"); }
		}
	}
}