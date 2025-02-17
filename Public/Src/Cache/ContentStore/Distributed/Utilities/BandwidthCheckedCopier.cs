﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.Utils;

namespace BuildXL.Cache.ContentStore.Distributed.Utilities
{
    /// <summary>
    /// File copier that times out copies based on their bandwidths.
    /// </summary>
    public class BandwidthCheckedCopier<T> : IFileCopier<T> where T : PathBase
    {
        private readonly IFileCopier<T> _inner;
        private readonly BandwidthChecker _checker;
        private readonly ILogger _logger;

        /// <nodoc />
        public BandwidthCheckedCopier(IFileCopier<T> inner, BandwidthChecker.Configuration config, ILogger logger)
        {
            _inner = inner;
            _checker = new BandwidthChecker(config);
            _logger = logger;
        }

        /// <inheritdoc />
        public Task<CopyFileResult> CopyToAsync(T sourcePath, Stream destinationStream, long expectedContentSize, CancellationToken cancellationToken)
        {
            var context = new OperationContext(new Context(_logger), cancellationToken);
            return _checker.CheckBandwidthAtIntervalAsync(context, token => _inner.CopyToAsync(sourcePath, destinationStream, expectedContentSize, token), destinationStream);
        }
    }
}
