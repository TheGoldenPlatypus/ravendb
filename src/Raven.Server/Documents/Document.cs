﻿using System;
using Raven.Abstractions.Data;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;

namespace Raven.Server.Documents
{
    public class Document
    {
        public const string NoCollectionSpecified = "Raven/Empty";
        public const string SystemDocumentsCollection = "Raven/SystemDocs";

        private ulong? _hash;

        public long Etag;
        public LazyStringValue Key;
        public long StorageId;
        public BlittableJsonReaderObject Data;

        public unsafe ulong DataHash
        {
            get
            {
                if (_hash.HasValue == false)
                    _hash = Hashing.XXHash64.Calculate(Data.BasePointer, Data.Size);

                return _hash.Value;
            }
        }

        public void EnsureMetadata()
        {
            DynamicJsonValue mutatedMetadata;
            BlittableJsonReaderObject metadata;
            if (Data.TryGet(Constants.Metadata, out metadata))
            {
                metadata.Modifications = mutatedMetadata = new DynamicJsonValue(metadata);
            }
            else
            {
                Data.Modifications = new DynamicJsonValue(Data)
                {
                    [Constants.Metadata] = mutatedMetadata = new DynamicJsonValue()
                };
            }

            mutatedMetadata[Constants.MetadataEtagId] = Etag;
            mutatedMetadata[Constants.MetadataDocId] = Key;

            _hash = null;
        }

        public void RemoveAllPropertiesExceptMetadata()
        {
            foreach (var property in Data.GetPropertyNames())
            {
                if (string.Equals(property, Constants.Metadata, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Data.Modifications == null)
                    Data.Modifications = new DynamicJsonValue(Data);

                Data.Modifications.Remove(property);
            }

            _hash = null;
        }

        public static string GetCollectionName(string key, BlittableJsonReaderObject document, out bool isSystemDocument)
        {
            if (key.StartsWith("Raven/", StringComparison.OrdinalIgnoreCase))
            {
                isSystemDocument = true;
                return SystemDocumentsCollection;
            }

            isSystemDocument = false;
            string collectionName;
            BlittableJsonReaderObject metadata;
            if (document.TryGet(Constants.Metadata, out metadata) == false ||
                metadata.TryGet(Constants.Headers.RavenEntityName, out collectionName) == false)
            {
                collectionName = NoCollectionSpecified;
            }
            return collectionName;
        }
    }
}