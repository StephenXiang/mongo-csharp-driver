﻿/* Copyright 2013-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver.Core.Async;
using MongoDB.Driver.Core.Bindings;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Servers;
using MongoDB.Driver.Core.WireProtocol;
using MongoDB.Driver.Core.WireProtocol.Messages.Encoders;

namespace MongoDB.Driver.Core.Operations
{
    /// <summary>
    /// Represents a Find operation.
    /// </summary>
    /// <typeparam name="TDocument">The type of the returned documents.</typeparam>
    public class FindOperation<TDocument> : QueryOperationBase, IReadOperation<IAsyncCursor<TDocument>>
    {
        // fields
        private bool _awaitData;
        private int? _batchSize;
        private readonly CollectionNamespace _collectionNamespace;
        private string _comment;
        private BsonDocument _criteria;
        private int? _limit;
        private TimeSpan? _maxTime;
        private readonly MessageEncoderSettings _messageEncoderSettings;
        private BsonDocument _modifiers;
        private bool _noCursorTimeout;
        private bool _partial;
        private BsonDocument _projection;
        private readonly IBsonSerializer<TDocument> _resultSerializer;
        private int? _skip;
        private BsonDocument _sort;
        private bool _tailable;

        // constructors
        public FindOperation(
            CollectionNamespace collectionNamespace,
            IBsonSerializer<TDocument> resultSerializer,
            MessageEncoderSettings messageEncoderSettings)
        {
            _collectionNamespace = Ensure.IsNotNull(collectionNamespace, "collectionNamespace");
            _resultSerializer = Ensure.IsNotNull(resultSerializer, "serializer");
            _messageEncoderSettings = messageEncoderSettings;
        }

        // properties
        public bool AwaitData
        {
            get { return _awaitData; }
            set { _awaitData = value; }
        }

        public int? BatchSize
        {
            get { return _batchSize; }
            set { _batchSize = Ensure.IsNullOrGreaterThanOrEqualToZero(value, "value"); }
        }

        public CollectionNamespace CollectionNamespace
        {
            get { return _collectionNamespace; }
        }

        public string Comment
        {
            get { return _comment; }
            set { _comment = value; }
        }

        public BsonDocument Criteria
        {
            get { return _criteria; }
            set { _criteria = value; }
        }

        public int? Limit
        {
            get { return _limit; }
            set { _limit = value; }
        }

        public TimeSpan? MaxTime
        {
            get { return _maxTime; }
            set { _maxTime = value; }
        }

        public MessageEncoderSettings MessageEncoderSettings
        {
            get { return _messageEncoderSettings; }
        }

        public BsonDocument Modifiers
        {
            get { return _modifiers; }
            set { _modifiers = value; }
        }

        public bool NoCursorTimeout
        {
            get { return _noCursorTimeout; }
            set { _noCursorTimeout = value; }
        }

        public bool Partial
        {
            get { return _partial; }
            set { _partial = value; }
        }

        public BsonDocument Projection
        {
            get { return _projection; }
            set { _projection = value; }
        }

        public IBsonSerializer<TDocument> ResultSerializer
        {
            get { return _resultSerializer; }
        }

        public int? Skip
        {
            get { return _skip; }
            set { _skip = Ensure.IsNullOrGreaterThanOrEqualToZero(value, "value"); }
        }

        public BsonDocument Sort
        {
            get { return _sort; }
            set { _sort = value; }
        }

        public bool Tailable
        {
            get { return _tailable; }
            set { _tailable = value; }
        }

        // methods
        public async Task<IAsyncCursor<TDocument>> ExecuteAsync(IReadBinding binding, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(binding, "binding");

            var slidingTimeout = new SlidingTimeout(timeout);

            using (var connectionSource = await binding.GetReadConnectionSourceAsync(slidingTimeout, cancellationToken))
            {
                var query = CreateWrappedQuery(connectionSource.ServerDescription, binding.ReadPreference);
                var protocol = CreateProtocol(query, binding.ReadPreference);
                var batch = await protocol.ExecuteAsync(connectionSource, slidingTimeout, cancellationToken);

                return new BatchCursor<TDocument>(
                    connectionSource.Fork(),
                    _collectionNamespace,
                    query,
                    batch.Documents,
                    batch.CursorId,
                    _batchSize ?? 0,
                    Math.Abs(_limit ?? 0),
                    _resultSerializer,
                    _messageEncoderSettings,
                    timeout,
                    cancellationToken);
            }
        }

        private int CalculateFirstBatchSize()
        {
            int firstBatchSize;

            var limit = _limit ?? 0;
            var batchSize = _batchSize ?? 0;
            if (limit < 0)
            {
                firstBatchSize = limit;
            }
            else if (limit == 0)
            {
                firstBatchSize = batchSize;
            }
            else if (batchSize == 0)
            {
                firstBatchSize = limit;
            }
            else if (limit < batchSize)
            {
                firstBatchSize = limit;
            }
            else
            {
                firstBatchSize = batchSize;
            }

            return firstBatchSize;
        }

        private QueryWireProtocol<TDocument> CreateProtocol(BsonDocument wrappedQuery, ReadPreference readPreference)
        {
            var slaveOk = readPreference != null && readPreference.ReadPreferenceMode != ReadPreferenceMode.Primary;
            var firstBatchSize = CalculateFirstBatchSize();

            return new QueryWireProtocol<TDocument>(
                _collectionNamespace,
                wrappedQuery,
                _projection,
                NoOpElementNameValidator.Instance,
                _skip ?? 0,
                firstBatchSize,
                slaveOk,
                _partial,
                _noCursorTimeout,
                _tailable,
                _awaitData,
                _resultSerializer,
                _messageEncoderSettings);
        }

        private BsonDocument CreateWrappedQuery(ServerDescription serverDescription, ReadPreference readPreference)
        {
            BsonDocument readPreferenceDocument = null;
            if (serverDescription.Type == ServerType.ShardRouter)
            {
                readPreferenceDocument = CreateReadPreferenceDocument(readPreference);
            }

            var wrappedQuery = new BsonDocument();
            if (_modifiers != null)
            {
                wrappedQuery.AddRange(_modifiers);
            }

            wrappedQuery["$query"] = _criteria ?? new BsonDocument();
            if(readPreferenceDocument != null)
            {
                wrappedQuery["$readPreference"] = readPreferenceDocument;
            }
            if(_sort != null)
            {
                wrappedQuery["$orderby"] = _sort;
            }
            if (_comment != null)
            {
                wrappedQuery["$comment"] = _comment;
            }
            if(_maxTime.HasValue)
            {
                wrappedQuery["$maxTimeMS"] = _maxTime.Value.TotalMilliseconds;
            }

            return wrappedQuery;
        }
    }
}
