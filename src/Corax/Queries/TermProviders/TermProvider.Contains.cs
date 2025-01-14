﻿using System;
using System.Collections.Generic;
using Corax.Mappings;
using Voron;
using Voron.Data.CompactTrees;

namespace Corax.Queries
{
    public struct ContainsTermProvider : ITermProvider
    {
        private readonly CompactTree _tree;
        private readonly IndexSearcher _searcher;
        private readonly FieldMetadata _field;
        private readonly CompactKey _term;

        private CompactTree.Iterator _iterator;
        public ContainsTermProvider(IndexSearcher searcher, CompactTree tree, FieldMetadata field, CompactKey term)
        {
            _tree = tree;
            _searcher = searcher;
            _field = field;
            _iterator = tree.Iterate();
            _iterator.Reset();
            _term = term;
        }

        public void Reset()
        {
            _iterator = _tree.Iterate();
            _iterator.Reset();
        }

        public bool Next(out TermMatch term)
        {
            var contains = _term.Decoded();
            while (_iterator.MoveNext(out var termScope, out var _))
            {
                var termSlice = termScope.Key.Decoded();
                if (!termSlice.Contains(contains))
                {
                    termScope.Dispose();
                    continue;
                }

                term = _searcher.TermQuery(_field, termScope.Key, _tree);
                return true;
            }

            term = TermMatch.CreateEmpty(_searcher, _searcher.Allocator);
            return false;
        }

        public QueryInspectionNode Inspect()
        {
            return new QueryInspectionNode($"{nameof(ContainsTermProvider)}",
                            parameters: new Dictionary<string, string>()
                            {
                                { "Field", _field.ToString() },
                                { "Term", _term.ToString()}
                            });
        }
    }
}
