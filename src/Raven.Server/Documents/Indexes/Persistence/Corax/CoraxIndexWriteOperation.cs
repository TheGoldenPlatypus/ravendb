﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Corax;
using Corax.Pipeline;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Server.Exceptions;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Logging;
using Sparrow.Server;
using Voron;
using Voron.Impl;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Indexes.Persistence.Corax
{
    public class CoraxIndexWriteOperation : IndexWriteOperationBase
    {
        public const int MaximumPersistedCapacityOfCoraxWriter = 512;
        protected const int DocumentBufferSize = 1024 * 1024 * 5;

        private readonly IndexWriter _indexWriter;
        private readonly CoraxDocumentConverterBase _converter;
        private readonly IndexFieldsMapping _knownFields;
        private readonly IDisposable _bufferScope;
        private readonly ByteString _buffer;
        private int _entriesCount = 0;

        public CoraxIndexWriteOperation(Index index, Transaction writeTransaction, CoraxDocumentConverterBase converter, Logger logger) : base(index, logger)
        {
            _bufferScope = writeTransaction.Allocator.Allocate(DocumentBufferSize, out _buffer);
            _converter = converter;
            _knownFields = _converter.GetKnownFields();
            try
            {
                _knownFields.UpdateAnalyzersInBindings(CoraxIndexingHelpers.CreateCoraxAnalyzers(writeTransaction.Allocator, index, index.Definition));
                _indexWriter = new IndexWriter(writeTransaction, _knownFields);
                _entriesCount = Convert.ToInt32(_indexWriter.GetNumberOfEntries());
            }
            catch (Exception e) when (e.IsOutOfMemory())
            {
                throw;
            }
            catch (Exception e)
            {
                throw new IndexWriteException(e);
            }
        }
        
        public override void Commit(IndexingStatsScope stats)
        {
            if (_indexWriter != null)
            {
                using (stats.For(IndexingOperation.Corax.Commit))
                {
                    _indexWriter.Commit();
                }
            }
        }

        public override void IndexDocument(LazyStringValue key, LazyStringValue sourceDocumentId, object document, IndexingStatsScope stats,
            JsonOperationContext indexContext)
        {
            EnsureValidStats(stats);
            _entriesCount++;
            Span<byte> data;
            LazyStringValue lowerId;

            using (Stats.ConvertStats.Start())
                data = _converter.SetDocumentFields(key, sourceDocumentId, document, indexContext, out lowerId, _buffer.ToSpan());

            if (data.IsEmpty)
                return;
            
            using (Stats.AddStats.Start())
                _indexWriter.Index(lowerId, data, _knownFields);

            stats.RecordIndexingOutput();
        }

        public override int EntriesCount() => _entriesCount;

        public override (long RamSizeInBytes, long FilesAllocationsInBytes) GetAllocations()
        {
            //todo maciej
            return (1024 * 1024, 1024 * 1024);
        }

        public override void Optimize()
        {
            // Lucene method
        }

        public override void Delete(LazyStringValue key, IndexingStatsScope stats)
        {
            EnsureValidStats(stats);
            using (Stats.DeleteStats.Start())
                if (_indexWriter.TryDeleteEntry(Constants.Documents.Indexing.Fields.DocumentIdFieldName, key.ToString()))
                {
                    _entriesCount--;
                }
        }

        public override void DeleteBySourceDocument(LazyStringValue sourceDocumentId, IndexingStatsScope stats)
        {
            throw new NotImplementedException();
        }

        public override void DeleteReduceResult(LazyStringValue reduceKeyHash, IndexingStatsScope stats)
        {
            EnsureValidStats(stats);

            if (_indexWriter.TryDeleteEntry(Constants.Documents.Indexing.Fields.ReduceKeyValueFieldName, reduceKeyHash.ToString()))
            {
                _entriesCount--;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureValidStats(IndexingStatsScope stats)
        {
            if (_statsInstance == stats)
                return;

            _statsInstance = stats;

            Stats.DeleteStats = stats.For(IndexingOperation.Corax.Delete, start: false);
            Stats.AddStats = stats.For(IndexingOperation.Corax.AddDocument, start: false);
            Stats.ConvertStats = stats.For(IndexingOperation.Corax.Convert, start: false);
        }
        
        public override void Dispose()
        {
            _bufferScope?.Dispose();
            _indexWriter?.Dispose();
            if (_converter.StringsListForEnumerableScope?.Capacity > MaximumPersistedCapacityOfCoraxWriter)
            {
                //We want to make sure we didn't persist too much memory for our enumerable writer.
                _converter.DoublesListForEnumerableScope = null;
                _converter.LongsListForEnumerableScope = null;
                _converter.StringsListForEnumerableScope = null;
                _converter.BlittableJsonReaderObjectsListForEnumerableScope = null;
            }
            else
            {
                _converter.DoublesListForEnumerableScope?.Clear();
                _converter.LongsListForEnumerableScope?.Clear();
                _converter.StringsListForEnumerableScope?.Clear();
                _converter.BlittableJsonReaderObjectsListForEnumerableScope?.Clear();
            }
        }
    }
}
