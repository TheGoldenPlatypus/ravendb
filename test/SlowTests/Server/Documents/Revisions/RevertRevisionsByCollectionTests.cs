﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.Replication;
using FastTests.Utils;
using Raven.Client;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Exceptions;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.ServerWide;
using Raven.Server.Documents;
using Raven.Server.Documents.Revisions;
using Raven.Server.ServerWide;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Xunit;
using Xunit.Abstractions;


namespace SlowTests.Server.Documents.Revisions
{
    public class RevertRevisionsByCollectionTests : ReplicationTestBase
    {
        public RevertRevisionsByCollectionTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public async Task RevertByCollection()
        {
            var collection = "companies";
            var company = new Company { Name = "Company Name" };
            var user = new User { Name = "User Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.StoreAsync(user);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    user.Name = "Shahar";
                    await session.StoreAsync(company);
                    await session.StoreAsync(user);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);

                RevertResult result;
                using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None))
                {
                    result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                        token: token, collection: collection);
                }

                Assert.Equal(2, result.ScannedRevisions);
                Assert.Equal(1, result.ScannedDocuments);
                Assert.Equal(1, result.RevertedDocuments);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(3, companiesRevisions.Count);

                    Assert.Equal("Company Name", companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos", companiesRevisions[1].Name);
                    Assert.Equal("Company Name", companiesRevisions[2].Name);

                    var usersRevisions = await session.Advanced.Revisions.GetForAsync<Company>(user.Id);
                    Assert.Equal(2, usersRevisions.Count);

                    Assert.Equal("Shahar", usersRevisions[0].Name);
                    Assert.Equal("User Name", usersRevisions[1].Name);
                }
            }
        }

        [Fact]
        public async Task RevertByWrongCollectionShouldThrow()
        {
            var collection = "companies1";
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                var db = await Databases.GetDocumentDatabaseInstanceFor(store);
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    using (var token = new OperationCancelToken(db.Configuration.Databases.OperationTimeout.AsTimeSpan, db.DatabaseShutdown, CancellationToken.None)) 
                    { 
                        var result = (RevertResult)await db.DocumentsStorage.RevisionsStorage.RevertRevisions(last, TimeSpan.FromMinutes(60), onProgress: null,
                            token: token, collection: collection);
                    }
                });

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(2, companiesRevisions.Count);

                    Assert.Equal("Hibernating Rhinos", companiesRevisions[0].Name);
                    Assert.Equal("Company Name", companiesRevisions[1].Name);
                }
            }
        }


        [Fact]
        public async Task RevertRevisionsEndPointCheck()
        {
            var collection = "companies";
            var company = new Company { Name = "Company Name" };
            var user = new User { Name = "User Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.StoreAsync(user);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    user.Name = "Shahar";
                    await session.StoreAsync(company);
                    await session.StoreAsync(user);
                    await session.SaveChangesAsync();
                }

                var operation = await store.Maintenance.SendAsync(new RevertRevisionsOperation(last, 60, collection));
                await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(3, companiesRevisions.Count);

                    Assert.Equal("Company Name", companiesRevisions[0].Name);
                    Assert.Equal("Hibernating Rhinos", companiesRevisions[1].Name);
                    Assert.Equal("Company Name", companiesRevisions[2].Name);

                    var usersRevisions = await session.Advanced.Revisions.GetForAsync<Company>(user.Id);
                    Assert.Equal(2, usersRevisions.Count);

                    Assert.Equal("Shahar", usersRevisions[0].Name);
                    Assert.Equal("User Name", usersRevisions[1].Name);
                }
            }
        }


        [Fact]
        public async Task RevertRevisionsEndPointCheckWrongCollection()
        {
            var collection = "companies1";
            var company = new Company { Name = "Company Name" };
            using (var store = GetDocumentStore())
            {
                DateTime last = default;
                await RevisionsHelper.SetupRevisions(Server.ServerStore, store.Database);
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                    last = DateTime.UtcNow;
                }

                using (var session = store.OpenAsyncSession())
                {
                    company.Name = "Hibernating Rhinos";
                    await session.StoreAsync(company);
                    await session.SaveChangesAsync();
                }

                await Assert.ThrowsAsync<BadResponseException>(async () =>
                {
                    var operation = await store.Maintenance.SendAsync(new RevertRevisionsOperation(last, 60, collection));
                    var result = await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                });

                using (var session = store.OpenAsyncSession())
                {
                    var companiesRevisions = await session.Advanced.Revisions.GetForAsync<Company>(company.Id);
                    Assert.Equal(2, companiesRevisions.Count);

                    Assert.Equal("Hibernating Rhinos", companiesRevisions[0].Name);
                    Assert.Equal("Company Name", companiesRevisions[1].Name);
                }
            }
        }

        private class RevertRevisionsOperation : IMaintenanceOperation<OperationIdResult>
        {
            private readonly RevertRevisionsRequest _request;
            private readonly string _collection;

            public RevertRevisionsOperation(DateTime time, long window, string collection = null)
            {
                _request = new RevertRevisionsRequest() { Time = time, WindowInSec = window };
                _collection = collection;
            }

            public RevertRevisionsOperation(RevertRevisionsRequest request)
            {
                _request = request ?? throw new ArgumentNullException(nameof(request));
            }

            public RavenCommand<OperationIdResult> GetCommand(DocumentConventions conventions, JsonOperationContext context)
            {
                return new RevertRevisionsCommand(_request, _collection);
            }

            private class RevertRevisionsCommand : RavenCommand<OperationIdResult>
            {
                private readonly RevertRevisionsRequest _request;
                private readonly string _collection;

                public RevertRevisionsCommand(RevertRevisionsRequest request, string collection = null)
                {
                    _request = request;
                    _collection = collection;
                }

                public override bool IsReadRequest => false;

                public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
                {
                    url = $"{node.Url}/databases/{node.Database}/revisions/revert";
                    if (_collection != null)
                    {
                        url += $"?collection={_collection}";
                    }

                    return new HttpRequestMessage
                    {
                        Method = HttpMethod.Post,
                        Content = new BlittableJsonContent(async stream => await ctx.WriteAsync(stream, DocumentConventions.Default.Serialization.DefaultConverter.ToBlittable(_request, ctx)).ConfigureAwait(false))
                    };
                }

                public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
                {
                    if (response == null)
                        ThrowInvalidResponse();

                    Result = DocumentConventions.Default.Serialization.DefaultConverter.FromBlittable<OperationIdResult>(response);
                }
            }
        }

    }
}