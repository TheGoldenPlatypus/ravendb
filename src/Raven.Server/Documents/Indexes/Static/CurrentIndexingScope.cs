﻿using System;
using System.Collections.Generic;
using Raven.Client.Linq;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Static
{
    public class CurrentIndexingScope : IDisposable
    {
        private readonly DocumentsStorage _documentsStorage;
        private readonly DocumentsOperationContext _documentsContext;
        private readonly string _collection;

        private DynamicDocumentObject _document;

        private DynamicNullObject _null;

        public HashSet<string> ReferencedCollections;

        public Dictionary<string, HashSet<string>> References;

        public Dictionary<string, Dictionary<string, long>> ReferenceEtags;

        [ThreadStatic]
        public static CurrentIndexingScope Current;

        public dynamic Source;

        public string SourceCollection;

        public CurrentIndexingScope(DocumentsStorage documentsStorage, DocumentsOperationContext documentsContext)
        {
            _documentsStorage = documentsStorage;
            _documentsContext = documentsContext;
        }

        public dynamic LoadDocument(LazyStringValue keyLazy, string keyString, string collectionName)
        {
            if (keyLazy == null && keyString == null)
                return Null();

            var source = Source;
            if (source == null)
                throw new ArgumentException("Cannot execute LoadDocument. Source is not set.");

            var id = source.__document_id as LazyStringValue;
            if (id == null)
                throw new ArgumentException("Cannot execute LoadDocument. Source does not have a key.");

            var key = keyLazy ?? keyString;
            if (id.Equals(key))
                return source;

            if (ReferencedCollections == null)
                ReferencedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (References == null)
                References = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> references;
            if (References.TryGetValue(id, out references) == false)
                References.Add(id, references = new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (ReferenceEtags == null)
                ReferenceEtags = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, long> referenceEtags;
            if (ReferenceEtags.TryGetValue(SourceCollection, out referenceEtags) == false)
                ReferenceEtags.Add(SourceCollection, referenceEtags = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

            ReferencedCollections.Add(collectionName);
            references.Add(key);

            var document = _documentsStorage.Get(_documentsContext, key);
            if (document == null)
            {
                referenceEtags.Add(key, 0);
                return Null();
            }

            referenceEtags.Add(key, document.Etag);

            if (_document == null)
                _document = new DynamicDocumentObject();

            _document.Set(document);

            return _document;
        }

        public void Dispose()
        {
            Current = null;
        }

        private DynamicNullObject Null()
        {
            return _null ?? (_null = new DynamicNullObject());
        }
    }
}