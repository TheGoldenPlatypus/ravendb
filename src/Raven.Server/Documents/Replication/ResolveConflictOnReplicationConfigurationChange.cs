﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents;
using Raven.Client.Documents.Exceptions;
using Raven.Server.Documents.Patch;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Logging;
using Voron;

namespace Raven.Server.Documents.Replication
{
    public class ResolveConflictOnReplicationConfigurationChange
    {
        private readonly DocumentDatabase _database;
        private readonly Logger _log;
        private readonly ReplicationLoader _replicationLoader;

        public Task ResolveConflictsTask = Task.CompletedTask;

        internal Dictionary<string, ScriptResolver> ScriptConflictResolversCache = new Dictionary<string, ScriptResolver>();
        public ConflictSolver ConflictSolver => _replicationLoader.ConflictSolverConfig;

        public ResolveConflictOnReplicationConfigurationChange(ReplicationLoader replicationLoader, Logger log)
        {
            _replicationLoader = replicationLoader ?? 
                throw new ArgumentNullException($"{nameof(ResolveConflictOnReplicationConfigurationChange)} must have replicationLoader instance");
            _database = _replicationLoader.Database;
            _log = log;
        }

        public long ConflictsCount => _database.DocumentsStorage?.ConflictsStorage?.ConflictsCount ?? 0;

        public void RunConflictResolversOnce()
        {
            UpdateScriptResolvers();

            if (ConflictsCount > 0 && ConflictSolver?.IsEmpty() == false)
            {
                try
                {
                    ResolveConflictsTask.Wait();
                }
                catch (Exception e)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Failed to wait for a previous task of automatic conflict resolution", e);
                }
                ResolveConflictsTask = Task.Run(ResolveConflictsInBackground);
            }
        }

        private async Task ResolveConflictsInBackground()
        {
            try
            {
                DocumentsOperationContext context;
                using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(out context))
                {
                    using (context.OpenReadTransaction())
                    {
                        var resolvedConflicts = new List<DocumentConflict>();

                        var hadConflicts = false;

                        foreach (var conflicts in _database.DocumentsStorage.ConflictsStorage.GetAllConflictsBySameKey(context))
                        {
                            if (_database.DatabaseShutdown.IsCancellationRequested)
                                break;

                            hadConflicts = true;

                            var collection = conflicts[0].Collection;

                            DocumentConflict resolved;
                            if (ScriptConflictResolversCache.TryGetValue(collection, out var scriptResolver) && scriptResolver != null)
                            {
                                if (TryResolveConflictByScriptInternal(
                                    context,
                                    scriptResolver,
                                    conflicts,
                                    collection,
                                    hasLocalTombstone: false,
                                    resolvedConflict: out resolved))
                                {
                                    resolvedConflicts.Add(resolved);

                                    //stats.AddResolvedBy(collection + " Script", conflictList.Count);
                                    continue;
                                }
                            }

                            if (TryResolveUsingDefaultResolverInternal(
                                context,
                                ConflictSolver?.DatabaseResolverId,
                                conflicts,
                                out resolved))
                            {
                                resolvedConflicts.Add(resolved);

                                //stats.AddResolvedBy("DatabaseResolver", conflictList.Count);
                                continue;
                            }

                            if (ConflictSolver?.ResolveToLatest == true)
                            {
                                resolvedConflicts.Add(ResolveToLatest(context, conflicts));

                                //stats.AddResolvedBy("ResolveToLatest", conflictList.Count);
                            }
                        }

                        if (hadConflicts == false || _database.DatabaseShutdown.IsCancellationRequested)
                            return;

                        if (resolvedConflicts.Count > 0)
                            await _database.TxMerger.Enqueue(new PutResolvedConflictsCommand(resolvedConflicts, this));
                    }
                }
            }
            catch (Exception e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Failed to run automatic conflict resolution", e);
            }
        }
        
        private class PutResolvedConflictsCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly List<DocumentConflict> _resolvedConflicts;
            private readonly ResolveConflictOnReplicationConfigurationChange _resolver;

            public PutResolvedConflictsCommand(List<DocumentConflict> resolvedConflicts, ResolveConflictOnReplicationConfigurationChange resolver)
            {
                _resolvedConflicts = resolvedConflicts;
                _resolver = resolver;
            }

            public override int Execute(DocumentsOperationContext context)
            {
                var count = 0;

                foreach (var resolved in _resolvedConflicts)
                {
                    _resolver.PutResolvedDocument(context, resolved);
                    count++;
                }

                return count;
            }
        }

        private void UpdateScriptResolvers()
        {
            if (ConflictSolver?.ResolveByCollection == null)            {
                if (ScriptConflictResolversCache.Count > 0)
                    ScriptConflictResolversCache = new Dictionary<string, ScriptResolver>();
                return;
            }
            var copy = new Dictionary<string, ScriptResolver>();
            foreach (var kvp in ConflictSolver.ResolveByCollection)
            {
                var collection = kvp.Key;
                var script = kvp.Value.Script;
                if (string.IsNullOrEmpty(script.Trim()))
                {
                    continue;
                }
                copy[collection] = new ScriptResolver
                {
                    Script = script
                };
            }
            ScriptConflictResolversCache = copy;
        }

        public bool TryResolveUsingDefaultResolverInternal(
            DocumentsOperationContext context,
            string resolver,
            IReadOnlyList<DocumentConflict> conflicts,
            out DocumentConflict resolved)
        {
            resolved = null;

            if (resolver == null)
                return false;

            long maxEtag = -1;
            foreach (var documentConflict in conflicts)
            {
                foreach (var changeVectorEntry in documentConflict.ChangeVector)
                {
                    if (changeVectorEntry.DbId.Equals(new Guid(resolver)))
                    {
                        if (changeVectorEntry.Etag == maxEtag)
                        {
                            // we have two documents with same etag of the leader
                            return false;
                        }

                        if (changeVectorEntry.Etag < maxEtag)
                            continue;

                        maxEtag = changeVectorEntry.Etag;
                        resolved = documentConflict;
                        break;
                    }
                }
            }

            if (resolved == null)
                return false;

            resolved.ChangeVector = ChangeVectorUtils.MergeVectors(conflicts.Select(c => c.ChangeVector).ToList());
            return true;
        }

        private bool ValidatedResolveByScriptInput(ScriptResolver scriptResolver,
            IReadOnlyList<DocumentConflict> conflicts,
            LazyStringValue collection)
        {
            if (scriptResolver == null)
                return false;
            if (collection == null)
                return false;
            if (conflicts.Count < 2)
                return false;

            foreach (var documentConflict in conflicts)
            {
                if (collection != documentConflict.Collection)
                {
                    var msg = $"All conflicted documents must have same collection name, but we found conflicted document in {collection} and an other one in {documentConflict.Collection}";
                    if (_log.IsInfoEnabled)
                        _log.Info(msg);

                    var differentCollectionNameAlert = AlertRaised.Create(
                        $"Script unable to resolve conflicted documents with the key {documentConflict.Key}",
                        msg,
                        AlertType.Replication,
                        NotificationSeverity.Error,
                        "Mismatched Collections On Replication Resolve"
                        );
                    _database.NotificationCenter.Add(differentCollectionNameAlert);
                    return false;
                }
            }

            return true;
        }

        public void PutResolvedDocument(
           DocumentsOperationContext context,
           DocumentConflict conflict)
        {
            if (conflict.Doc == null)
            {
                Slice loweredKey;
                using (Slice.External(context.Allocator, conflict.LoweredKey, out loweredKey))
                {
                    _database.DocumentsStorage.Delete(context, loweredKey, conflict.Key, null,
                        _database.Time.GetUtcNow().Ticks, conflict.ChangeVector, conflict.Collection);
                    return;
                }
            }

            // because we are resolving to a conflict, and putting a document will
            // delete all the conflicts, we have to create a copy of the document
            // in order to avoid the data we are saving from being removed while
            // we are saving it

            // the resolved document could be an update of the existing document, so it's a good idea to clone it also before updating.
            using (var clone = conflict.Doc.Clone(context))
            {
                // handle the case where we resolve a conflict for a document from a different collection
                DeleteDocumentFromDifferentCollectionIfNeeded(context, conflict);

                ReplicationUtils.EnsureCollectionTag(clone, conflict.Collection);
                _database.DocumentsStorage.Put(context, conflict.LoweredKey, null, clone, null, conflict.ChangeVector);
            }
        }

        private void DeleteDocumentFromDifferentCollectionIfNeeded(DocumentsOperationContext ctx, DocumentConflict conflict)
        {
            Document oldVersion;
            try
            {
                oldVersion = _database.DocumentsStorage.Get(ctx, conflict.LoweredKey);
            }
            catch (DocumentConflictException)
            {
                return; // if already conflicted, don't need to do anything
            }

            if (oldVersion == null)
                return;

            var oldVersionCollectionName = CollectionName.GetCollectionName(oldVersion.Data);
            if (oldVersionCollectionName.Equals(conflict.Collection, StringComparison.OrdinalIgnoreCase))
                return;

            _database.DocumentsStorage.DeleteWithoutCreatingTombstone(ctx, oldVersionCollectionName, oldVersion.StorageId, isTombstone: false);
        }

        public bool TryResolveConflictByScriptInternal(
            DocumentsOperationContext context,
            ScriptResolver scriptResolver,
            IReadOnlyList<DocumentConflict> conflicts,
            LazyStringValue collection,
            bool hasLocalTombstone, 
            out DocumentConflict resolvedConflict)
        {
            resolvedConflict = null;

            if (ValidatedResolveByScriptInput(scriptResolver, conflicts, collection) == false)
                return false;

            var patch = new PatchConflict(_database, conflicts);
            var updatedConflict = conflicts[0];
            var patchRequest = new PatchRequest
            {
                Script = scriptResolver.Script
            };
            if (patch.TryResolveConflict(context, patchRequest, out BlittableJsonReaderObject resolved) == false)
            {
                return false;
            }

            updatedConflict.Doc = resolved;
            updatedConflict.Collection = collection;
            updatedConflict.ChangeVector = ChangeVectorUtils.MergeVectors(conflicts.Select(c => c.ChangeVector).ToList());

            resolvedConflict = updatedConflict;

            return true;
        }

        public DocumentConflict ResolveToLatest(
            DocumentsOperationContext context,
            IReadOnlyList<DocumentConflict> conflicts)
        {
            var latestDoc = conflicts[0];
            var latestTime = latestDoc.LastModified.Ticks;

            foreach (var documentConflict in conflicts)
            {
                if (documentConflict.LastModified.Ticks > latestTime)
                {
                    latestDoc = documentConflict;
                    latestTime = documentConflict.LastModified.Ticks;
                }
            }

            latestDoc.ChangeVector = ChangeVectorUtils.MergeVectors(conflicts.Select(c => c.ChangeVector).ToList());

            return latestDoc;
        }
    }
}