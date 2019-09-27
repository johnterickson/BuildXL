// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Ipc.Common;
using BuildXL.Ipc.Interfaces;
using BuildXL.Utilities.Tasks;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.Drop.App.Core;
using Microsoft.VisualStudio.Services.Drop.WebApi;
using Microsoft.VisualStudio.Services.ItemStore.Common;

namespace Tool.ServicePipDaemon
{
    /// <summary>
    /// <see cref="IDropServiceClient"/> which will retry every operation in case <see cref="Microsoft.VisualStudio.Services.Common.VssUnauthorizedException"/> is caught.
    /// </summary>
    public sealed class ReloadingDropServiceClient : IDropServiceClient, IDedupStoreClient
    {
        private readonly ILogger m_logger;
        private readonly Reloader<(IDedupStoreClient, IDropServiceClient)> m_reloader;
        private readonly IEnumerable<TimeSpan> m_retryIntervals;
        private static readonly TimeSpan s_defaultOperationTimeout = TimeSpan.FromMinutes(5);

        // Default number and length of polling intervals - total time is approximately the sum of all these intervals.
        // NOTE: taken from DBS.ActionRetryer class, from CloudBuild.Core
        private static readonly IEnumerable<TimeSpan> s_defaultRetryIntervals = new[]
        {
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32),

            // Total just over 1 minute.
        };

        /// <summary>Used for testing.</summary>
        internal Reloader<(IDedupStoreClient,IDropServiceClient)> Reloader => m_reloader;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="clientConstructor">Target drop service client.</param>
        /// <param name="retryIntervals">How many times to retry and how much to wait between retries.</param>
        public ReloadingDropServiceClient(ILogger logger, Func<(IDedupStoreClient,IDropServiceClient)> clientConstructor, IEnumerable<TimeSpan> retryIntervals = null)
        {
            m_logger = logger;
            m_reloader = new Reloader<(IDedupStoreClient, IDropServiceClient)>(clientConstructor, destructor: client => client.Item2.Dispose());
            m_retryIntervals = retryIntervals ?? s_defaultRetryIntervals;
        }

        #region Retry Logic
        private async Task<T> RetryAsync<T>(
            string operationName,
            Func<(IDedupStoreClient, IDropServiceClient), CancellationToken, Task<T>> fn,
            CancellationToken cancellationToken,
            IEnumerator<TimeSpan> retryIntervalEnumerator = null,
            bool reloadFirst = false,
            Guid? operationId = null,
            TimeSpan? timeout = null)
        {
            operationId = operationId ?? Guid.NewGuid();
            retryIntervalEnumerator = retryIntervalEnumerator ?? m_retryIntervals.GetEnumerator();
            timeout = timeout ?? s_defaultOperationTimeout;

            try
            {
                using (CancellationTokenSource timeoutCancellationSource = new CancellationTokenSource())
                using (CancellationTokenSource innerCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationSource.Token))
                {
                    var instance = GetCurrentVersionedValue();

                    if (reloadFirst)
                    {
                        var reloaded = m_reloader.Reload(instance.Version);
                        m_logger.Warning("[{2}] Drop service client reloaded; new instance created: {0}, new client version: {1}", reloaded, m_reloader.CurrentVersion, operationId.Value);
                    }

                    m_logger.Verbose("[{2}] Invoking '{0}' against IDropServiceClient instance version {1}", operationName, instance.Version, operationId.Value);
                    return await WithTimeoutAsync(fn(instance.Value, innerCancellationSource.Token), timeout.Value, timeoutCancellationSource);
                }
            }
            catch (Exception e)
            {
                if (e is TimeoutException)
                {
                    m_logger.Warning("Timeout ({0}sec) happened while waiting {1}.", timeout.Value.TotalSeconds, operationName);
                }

                if (retryIntervalEnumerator.MoveNext())
                {
                    m_logger.Warning("[{2}] Waiting {1} before retrying on exception: {0}", e.ToString(), retryIntervalEnumerator.Current, operationId.Value);
                    await Task.Delay(retryIntervalEnumerator.Current);
                    return await RetryAsync(operationName, fn, cancellationToken, retryIntervalEnumerator, reloadFirst: true, operationId: operationId);
                }
                else
                {
                    m_logger.Error("[{1}] Failing because number of retries were exhausted.  Final exception: {0};", e.ToString(), operationId.Value);
                    throw;
                }
            }
        }

        private Task RetryAsync(string operationName, Func<(IDedupStoreClient, IDropServiceClient), CancellationToken, Task> fn, CancellationToken token)
        {
            return RetryAsync(
                operationName,
                async (client, t) =>
                {
                    await fn(client, t);
                    return Unit.Void;
                },
                token);
        }

        private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, CancellationTokenSource timeoutToken)
        {
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
                {
                    timeoutToken.Cancel();
                    throw new TimeoutException();
                }
            }

            return await task;
        }

        #endregion

        private Reloader<(IDedupStoreClient,IDropServiceClient)>.VersionedValue GetCurrentVersionedValue()
        {
            m_reloader.EnsureLoaded();
            return m_reloader.CurrentVersionedValue;
        }

        #region IDropServiceClient Interface Methods

        /// <inheritdoc />
        public Task RepairManifestAsync(string dropName, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.RepairManifestAsync),
                (client, ct) => client.Item2.RepairManifestAsync(dropName, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<Tuple<IEnumerable<BlobIdentifier>, AssociationsStatus>> AssociateAsync(string dropName, List<FileBlobDescriptor> preComputedBlobIds, bool abortIfAlreadyExists, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.AssociateAsync),
                (client, ct) => client.Item2.AssociateAsync(dropName, preComputedBlobIds, abortIfAlreadyExists, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<DropItem> CreateAsync(string dropName, bool isAppendOnly, DateTime? expirationDate, bool chunkDedup, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.CreateAsync),
                (client, ct) => client.Item2.CreateAsync(dropName, isAppendOnly, expirationDate, chunkDedup, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<bool> DeleteAsync(DropItem dropItem, bool synchronous, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.DeleteAsync),
                (client, ct) => client.Item2.DeleteAsync(dropItem, synchronous, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task DownloadAsync(string dropName, DropServiceClientDownloadContext downloadContext, CancellationToken cancellationToken, bool releaseLocalCache = false)
        {
            return RetryAsync(
                nameof(IDropServiceClient.DownloadAsync),
                (client, ct) => client.Item2.DownloadAsync(dropName, downloadContext, ct, releaseLocalCache),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task DownloadFilesAsync(IEnumerable<BlobToFileMapping> mappings, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.DownloadFilesAsync),
                (client, ct) => client.Item2.DownloadFilesAsync(mappings, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task FinalizeAsync(string dropName, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.FinalizeAsync),
                (client, ct) => client.Item2.FinalizeAsync(dropName, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<DropItem> GetDropAsync(string dropName, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.GetDropAsync),
                (client, ct) => client.Item2.GetDropAsync(dropName, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<Uri> GetDropUri(string dropName, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.GetDropUri),
                (client, ct) => client.Item2.GetDropUri(dropName, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IEnumerable<DropItem>> ListAsync(string dropNamePrefix, PathOptions pathOptions, bool includeNonFinalizedDrops, CancellationToken cancellationToken, RetrievalOptions retrievalOptions = RetrievalOptions.ExcludeSoftDeleted)
        {
            return RetryAsync(
                nameof(IDropServiceClient.ListAsync),
                (client, ct) => client.Item2.ListAsync(dropNamePrefix, pathOptions, includeNonFinalizedDrops, ct, retrievalOptions),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task UploadAndAssociateAsync(string dropName, List<FileBlobDescriptor> preComputedBlobIds, bool abortIfAlreadyExists, AssociationsStatus firstAssociationStatus, CancellationToken cancellationToken)
        {
            return RetryAsync(
                nameof(IDropServiceClient.UploadAndAssociateAsync),
                (client, ct) => client.Item2.UploadAndAssociateAsync(dropName, preComputedBlobIds, abortIfAlreadyExists, firstAssociationStatus, ct),
                cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_reloader.Dispose();
        }

        /// <inheritdoc />
        public Task<IAsyncEnumerator<IEnumerable<BlobToFileMapping>>> ListFilePagesAsync(
            string dropName,
            bool tryToRetrieveFromLocalCache,
            CancellationToken cancellationToken,
            bool allowPartial,
            IEnumerable<string> directories,
            bool recursive,
            bool getDownloadUris)
        {
            return RetryAsync(
                nameof(IDropServiceClient.ListFilePagesAsync),
                (client, ct) => client.Item2.ListFilePagesAsync(dropName, tryToRetrieveFromLocalCache, ct, allowPartial, directories, recursive, getDownloadUris),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task PublishAsync(
            string dropName,
            string sourceDirectory,
            bool abortIfAlreadyExists,
            List<FileBlobDescriptor> preComputedBlobIds,
            Action<FileBlobDescriptor> hashCompleteCallback,
            bool includeEmptyDirectories,
            bool lowercasePaths,
            CancellationToken cancellationToken)
        {
            return RetryAsync(
                 nameof(IDropServiceClient.PublishAsync),
                 (client, ct) => client.Item2.PublishAsync(dropName, sourceDirectory, abortIfAlreadyExists, preComputedBlobIds, hashCompleteCallback, includeEmptyDirectories, lowercasePaths, ct),
                 cancellationToken);
        }

        /// <inheritdoc />
        public Task UpdateExpirationAsync(string dropName, DateTime? expirationTime, CancellationToken cancellationToken)
        {
            return RetryAsync(
                    nameof(IDropServiceClient.UpdateExpirationAsync),
                    (client, ct) => client.Item2.UpdateExpirationAsync(dropName, expirationTime, ct),
                    cancellationToken);
        }

        /// <inheritdoc />
        public string GetVersionString()
        {
            return GetCurrentVersionedValue().Value.Item2.GetVersionString();
        }

        public string ParseDownloadStatistics(long totalContentBytes, long physicalContentBytesDownloaded, long compressionDownloadBytesSaved, long dedupDownloadBytesSaved, long chunksDownloaded)
        {
            throw new NotImplementedException();
        }

        public Task<IDisposable> AcquireParallelismTokenAsync()
        {
            throw new NotImplementedException();
        }

        public IDedupUploadSession CreateUploadSession(IDedupStoreClient NOT_USED, KeepUntilBlobReference keepUntilReference, IAppTraceSource tracer, IFileSystem fileSystem)
        {
            return RetryAsync<IDedupUploadSession>(
                nameof(IDedupStoreClient.CreateUploadSession),
                (client, ct) => Task.FromResult(client.Item1.CreateUploadSession(client.Item1, keepUntilReference, tracer, fileSystem)),
                CancellationToken.None).Result;
        }

        public Task DownloadToFileAsync(DedupIdentifier dedupId, string fullPath, GetDedupAsyncFunc dedupFetcher, Uri proxyUri, EdgeCache edgeCache, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<BuildXL.Cache.ContentStore.Hashing.DedupNode> DownloadToFileAsync(BuildXL.Cache.ContentStore.Hashing.DedupNode node, string fullPath, Uri proxyUri, EdgeCache edgeCache, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<MaybeCached<DedupCompressedBuffer>> GetChunkAsync(ChunkDedupIdentifier chunkId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<MaybeCached<DedupCompressedBuffer>> GetDedupAsync(DedupIdentifier dedupId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<BuildXL.Cache.ContentStore.Hashing.DedupNode> GetFilledNodesAsync(BuildXL.Cache.ContentStore.Hashing.DedupNode node, Uri proxyUri, EdgeCache edgeCache, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<MaybeCached<DedupCompressedBuffer>> GetNodeAsync(NodeDedupIdentifier nodeId, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<Dictionary<DedupIdentifier, GetDedupAsyncFunc>> GetDedupGettersAsync(ISet<DedupIdentifier> dedupIds, Uri proxyUri, EdgeCache edgeCache, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public void ResetDownloadStatistics()
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public uint AttemptNumber
        {
            get
            {
                return GetCurrentVersionedValue().Value.Item2.AttemptNumber;
            }

            set
            {
                GetCurrentVersionedValue().Value.Item2.AttemptNumber = value;
            }
        }

        /// <inheritdoc />
        public bool DisposeTelemetry
        {
            get
            {
                return GetCurrentVersionedValue().Value.Item2.DisposeTelemetry;
            }

            set
            {
                GetCurrentVersionedValue().Value.Item2.DisposeTelemetry = value;
            }
        }

        public IDedupStoreHttpClient Client => throw new NotImplementedException();

        public string DownloadStatistics => throw new NotImplementedException();

        public DownloadStatisticsSummary DownloadStatisticsSummary => throw new NotImplementedException();

        #endregion
    }
}
