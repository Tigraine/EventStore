﻿
namespace EventStore.Persistence.RavenPersistence
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Threading;
	using System.Transactions;
	using Indexes;
	using Raven.Client;
	using Raven.Client.Exceptions;
	using Raven.Client.Indexes;
	using Raven.Database.Data;
	using Raven.Database.Json;
	using Serialization;

	public class RavenPersistenceEngine : IPersistStreams
	{
		private readonly IDocumentStore store;
		private readonly ISerialize serializer;
		private readonly bool consistentQueries;
		private bool disposed;

		public RavenPersistenceEngine(IDocumentStore store, ISerialize serializer, bool consistentQueries)
		{
			this.store = store;
			this.serializer = serializer;
			this.consistentQueries = consistentQueries;
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
			this.store.Dispose();
		}

		public virtual void Initialize()
		{
			using (var scope = this.OpenCommandScope())
			{
				new RavenCommitByDate().Execute(this.store);
				new RavenCommitByRevisionRange().Execute(this.store);
				new RavenCommitsByDispatched().Execute(this.store);
				new RavenSnapshotByStreamIdAndRevision().Execute(this.store);
				new RavenStreamHeadBySnapshotAge().Execute(this.store);

				scope.Complete();
			}
		}

		public virtual IEnumerable<Commit> GetFrom(Guid streamId, int minRevision, int maxRevision)
		{
			return this.QueryCommits<RavenCommitByRevisionRange>(x =>
				x.StreamId == streamId && x.StreamRevision >= minRevision && x.StartingStreamRevision <= maxRevision);
		}

		public virtual IEnumerable<Commit> GetFrom(DateTime start)
		{
			return this.QueryCommits<RavenCommitByDate>(x => x.CommitStamp >= start);
		}

		public virtual void Commit(Commit attempt)
		{
			try
			{
				using (var scope = this.OpenCommandScope())
				using (var session = this.store.OpenSession())
				{
					session.Advanced.UseOptimisticConcurrency = true;
					session.Store(attempt.ToRavenCommit(this.serializer));
					session.SaveChanges();
					scope.Complete();
				}

				this.SaveStreamHead(attempt.ToRavenStreamHead());
			}
			catch (NonUniqueObjectException e)
			{
				throw new DuplicateCommitException(e.Message, e);
			}
			catch (Raven.Http.Exceptions.ConcurrencyException)
			{
				var savedCommit = this.LoadSavedCommit(attempt);
				if (savedCommit.CommitId == attempt.CommitId)
					throw new DuplicateCommitException();

				throw new ConcurrencyException();
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		public virtual IEnumerable<Commit> GetUndispatchedCommits()
		{
			return this.QueryCommits<RavenCommitsByDispatched>(c => c.Dispatched == false);
		}

		public virtual void MarkCommitAsDispatched(Commit commit)
		{
			if (commit == null)
				throw new ArgumentNullException("commit");

			var patch = new PatchRequest
			{
				Type = PatchCommandType.Set,
				Name = "Dispatched",
				Value = true
			};
			var data = new PatchCommandData
			{
				Key = commit.ToRavenCommitId(),
				Patches = new[] { patch }
			};

			try
			{
				using (var scope = this.OpenCommandScope())
				using (var session = this.store.OpenSession())
				{
					session.Advanced.DatabaseCommands.Batch(new[] { data });
					session.SaveChanges();
					scope.Complete();
				}
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
		{
			return this.Query<RavenStreamHead, RavenStreamHeadBySnapshotAge>(s => s.SnapshotAge >= maxThreshold)
				.Select(s => s.ToStreamHead());
		}

		public virtual Snapshot GetSnapshot(Guid streamId, int maxRevision)
		{
			return this.Query<RavenSnapshot, RavenSnapshotByStreamIdAndRevision>(
					x => x.StreamId == streamId && x.StreamRevision <= maxRevision)
				.OrderByDescending(x => x.StreamRevision)
				.FirstOrDefault()
				.ToSnapshot(this.serializer);
		}

		public virtual bool AddSnapshot(Snapshot snapshot)
		{
			if (snapshot == null)
				return false;

			try
			{
				using (var scope = this.OpenCommandScope())
				using (var session = this.store.OpenSession())
				{
					var ravenSnapshot = snapshot.ToRavenSnapshot(this.serializer);
					session.Store(ravenSnapshot);
					session.SaveChanges();
					scope.Complete();
				}

				this.SaveStreamHead(snapshot.ToRavenStreamHead());

				return true;
			}
			catch (Raven.Http.Exceptions.ConcurrencyException)
			{
				return false;
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		private RavenCommit LoadSavedCommit(Commit attempt)
		{
			try
			{
				using (this.OpenQueryScope())
				using (var session = this.store.OpenSession())
					return session.Load<RavenCommit>(attempt.ToRavenCommitId());
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		private IEnumerable<Commit> QueryCommits<TIndex>(Expression<Func<RavenCommit, bool>> query)
			where TIndex : AbstractIndexCreationTask, new()
		{
			return this.Query<RavenCommit, TIndex>(query).Select(x => x.ToCommit(this.serializer));
		}

		private IEnumerable<T> Query<T, TIndex>(Expression<Func<T, bool>> query)
			where TIndex : AbstractIndexCreationTask, new()
		{
			try
			{
				using (this.OpenQueryScope())
				using (var session = this.store.OpenSession())
					return session.Query<T, TIndex>()
						.Customize(x => { if (this.consistentQueries) x.WaitForNonStaleResults(); })
						.Where(query);
			}
			catch (Exception e)
			{
				throw new StorageException(e.Message, e);
			}
		}

		private void SaveStreamHead(RavenStreamHead streamHead)
		{
			ThreadPool.QueueUserWorkItem(x => this.SaveStreamHeadAsync(streamHead), null);
		}
		private void SaveStreamHeadAsync(RavenStreamHead streamHead)
		{
			using (var scope = this.OpenCommandScope())
			using (var session = this.store.OpenSession())
			{
				session.Advanced.UseOptimisticConcurrency = false;
				session.Store(streamHead);
				session.SaveChanges();
				scope.Complete();
			}
		}
		protected virtual TransactionScope OpenQueryScope()
		{
			return this.OpenCommandScope();
		}
		protected virtual TransactionScope OpenCommandScope()
		{
			return new TransactionScope(TransactionScopeOption.Suppress);
		}
	}
}