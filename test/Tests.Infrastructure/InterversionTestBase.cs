﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Extensions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Util;
using Raven.Server.Config;
using Raven.Server.Utils;
using Sparrow.Platform;
using Tests.Infrastructure.InterversionTest;
using Xunit;
using Xunit.Abstractions;

namespace Tests.Infrastructure
{
    public abstract class InterversionTestBase : ClusterTestBase
    {
        protected InterversionTestBase(ITestOutputHelper output) : base(output)
        {
        }

        public class ProcessNode
        {
            public string Version;
            public Process Process;
            public string Url;
            public string ServerPath;
            public string DataDir;
        }

        private static readonly ConcurrentBag<Process> _allLaunchedServerProcesses = new ConcurrentBag<Process>();

        private readonly HashSet<Process> _testInstanceServerProcesses = new HashSet<Process>();

        private static readonly ServerBuildRetriever _serverBuildRetriever = new ServerBuildRetriever();

        protected DocumentStore GetDocumentStore(
            string serverVersion,
            InterversionTestOptions options = null,
            [CallerMemberName] string database = null)
        {
            return AsyncHelpers.RunSync(() => GetDocumentStoreAsync(serverVersion, options, database));
        }

        protected async Task<DocumentStore> GetDocumentStoreAsync(
            string serverVersion,
            InterversionTestOptions options = null,
            [CallerMemberName] string database = null,
            CancellationToken token = default)
        {
            var processNode = await GetServerAsync(serverVersion, options, database, token);

            options = options ?? InterversionTestOptions.Default;
            var name = GetDatabaseName(database);

            if (options.ModifyDatabaseName != null)
                name = options.ModifyDatabaseName(name) ?? name;

            var store = new DocumentStore
            {
                Urls = new[] { processNode.Url },
                Database = name
            };

            options.ModifyDocumentStore?.Invoke(store);

            store.Initialize();

            var settings = new Dictionary<string, string>
            {
                [RavenConfiguration.GetKey(x => x.Replication.ReplicationMinimalHeartbeat)] = "1",
                [RavenConfiguration.GetKey(x => x.Replication.RetryReplicateAfter)] = "1",
                [RavenConfiguration.GetKey(x => x.Core.RunInMemory)] = "true",
                [RavenConfiguration.GetKey(x => x.Core.ThrowIfAnyIndexCannotBeOpened)] = "true",
                [RavenConfiguration.GetKey(x => x.Indexing.MinNumberOfMapAttemptsAfterWhichBatchWillBeCanceledIfRunningLowOnMemory)] = int.MaxValue.ToString()
            };

            if (options.CreateShardedDatabase)
            {
                var shardingDoc = new ShardingDatabaseRecord(name)
                {
                    Settings = settings,
                    Sharding = new ShardingConfiguration
                    {
                        Shards = new Dictionary<int, DatabaseTopology>()
                        {
                            { 0, new DatabaseTopology() }, { 1, new DatabaseTopology() }, { 2, new DatabaseTopology() },
                        }
                    }
                };

                options.ModifyDatabaseRecord?.Invoke(shardingDoc);
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(shardingDoc, options.ReplicationFactor), token);
            }
            else if (options.CreateDatabase)
            {
                var doc = new DatabaseRecord(name) { Settings = settings };

                options.ModifyDatabaseRecord?.Invoke(doc);
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(doc, options.ReplicationFactor), token);
            }

            store.AfterDispose += (sender, e) =>
            {
                KillSlavedServerProcess(processNode.Process);
            };

            return store;
        }


        protected async Task<ProcessNode> GetServerAsync(
            string serverVersion,
            InterversionTestOptions options = null,
            [CallerMemberName] string database = null,
            CancellationToken token = default)
        {
            var serverBuildInfo = ServerBuildDownloadInfo.Create(serverVersion);
            var serverPath = await _serverBuildRetriever.GetServerPath(serverBuildInfo, token);
            var testServerPath = NewDataPath(prefix: serverVersion);
            CopyFilesRecursively(new DirectoryInfo(serverPath), new DirectoryInfo(testServerPath));

            var locator = new ConfigurableRavenServerLocator(Path.Combine(testServerPath, "Server"), serverVersion);

            var result = await RunServer(locator);
            return new ProcessNode
            {
                Process = result.ServerProcess,
                Url = result.ServerUrl,
                Version = serverVersion,
                ServerPath = testServerPath,
                DataDir = locator.DataDir
            };
        }

        protected async Task UpgradeServerAsync(
            string toVersion,
            ProcessNode node,
            CancellationToken token = default)
        {
            KillSlavedServerProcess(node.Process);

            if (toVersion == "current")
            {
                RunLocalServer(node);
                return;
            }

            var serverBuildInfo = ServerBuildDownloadInfo.Create(toVersion);
            var serverPath = await _serverBuildRetriever.GetServerPath(serverBuildInfo, token);
            CopyFilesRecursively(new DirectoryInfo(serverPath), new DirectoryInfo(node.ServerPath));

            var locator = new ConfigurableRavenServerLocator(Path.Combine(node.ServerPath, "Server"), toVersion, node.DataDir, node.Url);

            var result = await RunServer(locator);
            Assert.Equal(node.Url, result.ServerUrl);

            node.Process = result.ServerProcess;
            node.Version = toVersion;
        }

        private void RunLocalServer(ProcessNode node)
        {
            var options = new ServerCreationOptions
            {
                CustomSettings = new Dictionary<string, string>
                {
                    [RavenConfiguration.GetKey(x => x.Core.ServerUrls)] = node.Url,
                    [RavenConfiguration.GetKey(x => x.Core.DataDirectory)] = node.DataDir,
                },
                RunInMemory = false,
                DeletePrevious = false
            };

            var server = GetNewServer(options);
            server.ServerStore.Engine.InMemoryDebug.IsInterVersionTest = true;

            Servers.Add(server);

            node.Process = null;
            node.ServerPath = null;
            node.Version = "current";
        }

        public static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (var dir in source.GetDirectories())
                CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
            foreach (var file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name), overwrite: true);
        }

        private async Task<(string ServerUrl, Process ServerProcess)> RunServer(ConfigurableRavenServerLocator locator)
        {
            var process = RunServerProcess(locator);

            string url = null;
            var startupDuration = Stopwatch.StartNew();

            var outputString = await ReadOutput(process.StandardOutput, startupDuration, async (line, builder) =>
            {
                if (line == null)
                {
                    var errorString = await ReadOutput(process.StandardError, startupDuration, null).ConfigureAwait(false);

                    KillSlavedServerProcess(process);

                    throw new InvalidOperationException(BuildStartupExceptionMessage(builder.ToString(), errorString));
                }

                const string prefix = "Server available on: ";
                if (line.StartsWith(prefix))
                {
                    url = line.Substring(prefix.Length);
                    return true;
                }

                return false;
            }).ConfigureAwait(false);

            if (url == null)
            {
                var errorString = await ReadOutput(process.StandardError, startupDuration, null).ConfigureAwait(false);

                KillSlavedServerProcess(process);

                throw new InvalidOperationException(BuildStartupExceptionMessage(outputString, errorString));
            }

            return (url, process);
        }

        private static string BuildStartupExceptionMessage(string outputString, string errorString)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unable to start the RavenDB Server");

            if (string.IsNullOrWhiteSpace(errorString) == false)
            {
                sb.AppendLine("Error:");
                sb.AppendLine(errorString);
            }

            if (string.IsNullOrWhiteSpace(outputString) == false)
            {
                sb.AppendLine("Output:");
                sb.AppendLine(outputString);
            }

            return sb.ToString();
        }

        protected static void KillSlavedServerProcess(Process process)
        {
            if (process == null || process.HasExited)
                return;

            try
            {
                process.Kill();
                if (process.WaitForExit((int)TimeSpan.FromMinutes(1).TotalMilliseconds) == false)
                {
                    throw new TimeoutException("Failed to wait for the process to exit.");
                }
            }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        private static async Task<string> ReadOutput(StreamReader output, Stopwatch startupDuration, Func<string, StringBuilder, Task<bool>> onLine)
        {
            var sb = new StringBuilder();

            Task<string> readLineTask = null;
            while (true)
            {
                if (readLineTask == null)
                    readLineTask = output.ReadLineAsync();

                var hasResult = await readLineTask.WaitWithTimeout(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                //if (startupDuration.Elapsed > options.MaxServerStartupTimeDuration)
                //    return null;

                if (hasResult == false)
                    continue;

                var line = readLineTask.Result;

                readLineTask = null;

                if (line != null)
                    sb.AppendLine(line);

                var shouldStop = false;
                if (onLine != null)
                    shouldStop = await onLine(line, sb).ConfigureAwait(false);

                if (shouldStop)
                    break;

                if (line == null)
                    break;
            }

            return sb.ToString();
        }

        private Process RunServerProcess(ConfigurableRavenServerLocator locator)
        {
            var process = RavenServerRunner<ConfigurableRavenServerLocator>.Run(locator);

            _allLaunchedServerProcesses.Add(process);
            _testInstanceServerProcesses.Add(process);
            return process;
        }

        private static void ReportError(Exception e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));

            var msg = $"{DateTime.Now}: {e}\r\n";
            Console.WriteLine(msg);
        }

        protected override void Dispose(ExceptionAggregator exceptionAggregator)
        {
            foreach (var serverProcess in _testInstanceServerProcesses)
            {
                exceptionAggregator.Execute(() =>
                {
                    if (serverProcess.HasExited == false)
                        serverProcess.Kill();
                });
            }

            base.Dispose(exceptionAggregator);
        }

        public async Task<bool> WaitForDocumentInClusterAsync<T>(string docId, Func<T, bool> predicate, TimeSpan timeout, List<DocumentStore> stores, string database = null)
        {
            var tasks = new List<Task<bool>>();

            foreach (var store in stores)
                tasks.Add(Task.Run(() => WaitForDocument(store, docId, predicate, (int)timeout.TotalMilliseconds, database)));

            await Task.WhenAll(tasks);

            return tasks.All(x => x.Result);
        }

        protected async Task<string> GetLastNightlyVersion(string version)
        {
            return await LastRavenDbVersion("Nightly", version);
        }

        protected async Task<string> GetLastStableVersion(string version)
        {
            return await LastRavenDbVersion("Stable", version);
        }

        private static async Task<string> LastRavenDbVersion(string type, string version)
        {
            using var client = new HttpClient();
            var url = new Uri("https://ravendb.net/wp-json/ravendb/downloads");
            var rawVersionRespond = (await client.GetAsync(url)).Content.ReadAsStringAsync().Result;
            dynamic versionRespond = JsonConvert.DeserializeObject<ExpandoObject>(rawVersionRespond);

            var ravenDbBuilds = ((IEnumerable<dynamic>)versionRespond.downloadsInfo.ravenDbBuilds);
            var platform = string.Empty;
            if (PlatformDetails.RunningOnPosix == false)
            {
                platform = PlatformDetails.Is32Bits ? "WindowsX86" : "WindowsX64";
            }
            else if (PlatformDetails.RunningOnLinux)
            {
                if (PlatformDetails.Is32Bits == false)
                {
                    platform = "LinuxX64";
                }
            }

            if (string.IsNullOrEmpty(platform))
            {
                throw new InvalidOperationException("Expected to run on WindowsX86, WindowsX64 or LinuxX64");
            }

            var dynamic = ravenDbBuilds
                .Where(x => x.platform == platform && x.branch == type && x.version.StartsWith(version)).OrderByDescending(x => x.publishedAt).FirstOrDefault();

            Assert.NotNull(dynamic);

            return dynamic.version;
        }

        public class InterversionTestOptions
        {
            private readonly bool _frozen;

            private bool _createDatabase;
            private bool _createShardedDatabase;
            private int _replicationFactor;
            private Action<DocumentStore> _modifyDocumentStore;
            private Action<DatabaseRecord> _modifyDatabaseRecord;
            private Func<string, string> _modifyDatabaseName;

            public static readonly InterversionTestOptions Default = new InterversionTestOptions(true);

            public InterversionTestOptions() : this(false)
            {
            }

            private InterversionTestOptions(bool frozen)
            {
                CreateDatabase = true;
                ReplicationFactor = 1;

                _frozen = frozen;
            }

            public Func<string, string> ModifyDatabaseName
            {
                get => _modifyDatabaseName;
                set
                {
                    AssertNotFrozen();
                    _modifyDatabaseName = value;
                }
            }

            public Action<DatabaseRecord> ModifyDatabaseRecord
            {
                get => _modifyDatabaseRecord;
                set
                {
                    AssertNotFrozen();
                    _modifyDatabaseRecord = value;
                }
            }

            public Action<DocumentStore> ModifyDocumentStore
            {
                get => _modifyDocumentStore;
                set
                {
                    AssertNotFrozen();
                    _modifyDocumentStore = value;
                }
            }

            public int ReplicationFactor
            {
                get => _replicationFactor;
                set
                {
                    AssertNotFrozen();
                    _replicationFactor = value;
                }
            }

            public bool CreateDatabase
            {
                get => _createDatabase;
                set
                {
                    AssertNotFrozen();
                    _createDatabase = value;
                }
            }

            public bool CreateShardedDatabase
            {
                get => _createShardedDatabase;
                set
                {
                    AssertNotFrozen();
                    _createShardedDatabase = value;
                }
            }

            private void AssertNotFrozen()
            {
                if (_frozen)
                    throw new InvalidOperationException("Options are frozen and cannot be changed.");
            }
        }

        protected class ShardingDatabaseRecord : DatabaseRecord
        {
            public ShardingConfiguration Sharding;

            public ShardingDatabaseRecord(string databaseName) : base(databaseName)
            {
            }
        }

        public sealed class ShardingConfiguration
        {
            public Dictionary<int, DatabaseTopology> Shards;
        }
    }
}
