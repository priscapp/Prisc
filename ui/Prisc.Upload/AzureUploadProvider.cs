﻿using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Prisc.Upload.Accelerate;

namespace Prisc.Upload
{
    public class AzureUploadProvider : UploadProviderBase, IAccelerateProvider
    {
        public override string Name => "Azure Storage";
        public override string Description => "Uploads any file to a public Azure container";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;
        public override Stream Icon => new Resource().AzureIcon;
        public override bool SupportsUnseekableUpload => true;

        public string ConnectionString
        {
            get => _connectionString;
            set
            {
                if (value != _connectionString)
                {
                    _connectionString = value;
                    OnPropertyChanged(nameof(ConnectionString));
                }
            }
        }

        public string ContainerName
        {
            get => _containerName;
            set
            {
                if (value != _containerName)
                {
                    _containerName = value;
                    OnPropertyChanged(nameof(ContainerName));
                }
            }
        }

        public string CustomDomain
        {
            get => _customDomain;
            set
            {
                if (value != _customDomain)
                {
                    _customDomain = value;
                    OnPropertyChanged(nameof(CustomDomain));
                }
            }
        }

        [Description("Route uploads through clwd.app so the link is ready immediately.")]
        public bool AccelerateUploads
        {
            get => _accelerateUploads;
            set => Set(ref _accelerateUploads, value);
        }

        [Description("Server used for accelerated uploads.")]
        public string AccelerateServerUrl
        {
            get => _accelerateServerUrl;
            set => Set(ref _accelerateServerUrl, value);
        }

        private readonly IMimeProvider _mimeDb;

        const string AZURE_SERVICE_VERSION = "2019-12-12";
        private string _connectionString;
        private string _containerName;
        private string _customDomain;
        private bool _accelerateUploads = true;
        private string _accelerateServerUrl = "https://clwd.app";

        public AzureUploadProvider() : base()
        {
            _mimeDb = new MimeProvider();
        }

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            if (!fileStream.CanSeek)
                return await UploadUnseekableAsync(fileStream, progress, uploadName, cancelToken);

            var key = GetNewBlobKey();
            var blob = (await PrepareContainerAsync(key, cancelToken)).GetBlobClient(key);
            var mimeType = GetMimeType(uploadName);

            await blob.UploadAsync(fileStream, new BlobUploadOptions
            {
                HttpHeaders = GetHttpHeaders(uploadName, mimeType),
                ProgressHandler = progress == null ? null : new ProgressAdapter(progress),
            }, cancelToken);

            return GetResult(blob, uploadName, mimeType);
        }

        public override Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, UploadUrlHandler urlAvailable,
            string uploadName, CancellationToken cancelToken)
            => AccelerateUploads
                ? UploadAcceleratedAsync(fileStream, progress, urlAvailable, uploadName, cancelToken)
                : UploadAsync(fileStream, progress, uploadName, cancelToken);

        /// <summary>Streams a payload of unknown length as a sequence of 8 MiB blocks committed
        /// with a final block list, so nothing is ever spooled to disk.</summary>
        private async Task<UploadResult> UploadUnseekableAsync(Stream fileStream, UploadProgressHandler progress, string uploadName,
            CancellationToken cancelToken)
        {
            const int blockBufferSize = 8 * 1024 * 1024;

            var key = GetNewBlobKey();
            var blob = (await PrepareContainerAsync(key, cancelToken)).GetBlockBlobClient(key);
            var mimeType = GetMimeType(uploadName);

            var blockIds = new List<string>();
            var buffer = new byte[blockBufferSize];
            long uploaded = 0;

            while (true)
            {
                var filled = await FillBufferAsync(fileStream, buffer, cancelToken);
                if (filled == 0)
                    break;

                // block ids must be base64 strings of equal pre-encoding length
                var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(blockIds.Count.ToString("d6")));
                await blob.StageBlockAsync(blockId, new MemoryStream(buffer, 0, filled, false), cancellationToken: cancelToken);

                blockIds.Add(blockId);
                uploaded += filled;
                progress?.Invoke(uploaded);

                if (filled < buffer.Length)
                    break;
            }

            await blob.CommitBlockListAsync(blockIds, new CommitBlockListOptions
            {
                HttpHeaders = GetHttpHeaders(uploadName, mimeType),
            }, cancelToken);

            return GetResult(blob, uploadName, mimeType);
        }

        private static async Task<int> FillBufferAsync(Stream stream, byte[] buffer, CancellationToken cancelToken)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancelToken);
                if (read == 0)
                    break;
                filled += read;
            }

            return filled;
        }

        private async Task<UploadResult> UploadAcceleratedAsync(Stream fileStream, UploadProgressHandler progress,
            UploadUrlHandler urlAvailable, string uploadName, CancellationToken cancelToken)
        {
            var mimeType = GetMimeType(uploadName);

            // null for a non-seekable source: the SAS destination is count-free, so unknown-length
            // sessions differ only in the create request and the ?final=1 marking of the last chunk.
            long? contentLength = fileStream.CanSeek ? fileStream.Length : null;

            var key = GetNewBlobKey();
            var blob = GetContainerClient().GetBlobClient(key);

            // a blob-level SAS with create+write only — the server never sees the account key.
            var sasUri = blob.GenerateSasUri(BlobSasPermissions.Create | BlobSasPermissions.Write,
                DateTimeOffset.UtcNow.AddHours(48));

            var descriptor = new DestinationDescriptor
            {
                Type = "azure-blob",
                BlobSasUrl = sasUri.ToString(),
                FinalUrl = GetPublicUrl(blob),
            };

            return await AcceleratedUploadRunner.RunAsync(
                AccelerateServerUrl, descriptor, fileStream, mimeType, contentLength, uploadName, blob.Name,
                AcceleratedUploadClient.DefaultChunkSize, this, null, progress, urlAvailable, cancelToken);
        }

        public override bool CanDelete(UploadDeleteInfo info)
            => !String.IsNullOrEmpty(info.UploadKey)
               && !String.IsNullOrEmpty(ConnectionString)
               && !String.IsNullOrEmpty(ContainerName);

        public override async Task DeleteAsync(UploadDeleteInfo info, CancellationToken cancelToken)
        {
            // delete the destination blob with the account's own credentials (as always).
            var blob = GetContainerClient().GetBlobClient(info.UploadKey);
            await blob.DeleteIfExistsAsync(cancellationToken: cancelToken);

            // accelerated records also carry a clwd.app short link — remove it too.
            if (AcceleratedDeleteToken.TryParse(info.DeleteKey, out var id, out var token))
                await AcceleratedUploadClient.DeleteAsync(AccelerateServerUrl, id, token, cancelToken);
        }

        private string GetNewBlobKey() => Guid.NewGuid().ToString().Replace("-", "");

        private BlobContainerClient GetContainerClient()
            => new BlobServiceClient(ConnectionString).GetBlobContainerClient(ContainerName);

        /// <summary>Validates the configured container and that <paramref name="key"/> is still free.</summary>
        private async Task<BlobContainerClient> PrepareContainerAsync(string key, CancellationToken cancelToken)
        {
            var storage = new BlobServiceClient(ConnectionString);

            // anonymous reads of the public blobs are served at the account's default service
            // version; pin a modern one so browsers get range requests and Content-Disposition.
            BlobServiceProperties serviceProperties = await storage.GetPropertiesAsync(cancelToken);
            if (serviceProperties.DefaultServiceVersion != AZURE_SERVICE_VERSION)
            {
                serviceProperties.DefaultServiceVersion = AZURE_SERVICE_VERSION;
                await storage.SetPropertiesAsync(serviceProperties, cancelToken);
            }

            var container = storage.GetBlobContainerClient(ContainerName);
            if (!await container.ExistsAsync(cancelToken))
                throw new InvalidOperationException("Configured Azure storage container does not exist");

            var existing = container.GetBlobClient(key);
            if (await existing.ExistsAsync(cancelToken))
                throw new InvalidOperationException("A blob with this key already exists");

            return container;
        }

        private string GetMimeType(string fileName)
            => _mimeDb.GetMimeFromExtension(Path.GetExtension(fileName)).ContentType;

        // we want to give the browser a filename hint, and also tell it to render it inline (if it can).
        // if it can't be rendered inline, the browser will just download it anyway.
        private static BlobHttpHeaders GetHttpHeaders(string fileName, string mimeType) => new()
        {
            ContentDisposition = $"inline; filename=\"{fileName}\"",
            ContentType = mimeType,
        };

        private string GetPublicUrl(BlobBaseClient blob)
            => String.IsNullOrWhiteSpace(CustomDomain)
                ? blob.Uri.ToString()
                : $"https://{CustomDomain}/{ContainerName}/{blob.Name}";

        private UploadResult GetResult(BlobBaseClient blob, string fileName, string mimeType)
        {
            return new UploadResult()
            {
                Provider = this,
                PublicUrl = GetPublicUrl(blob),
                FileName = fileName,
                ContentType = mimeType,
                UploadKey = blob.Name,
                UploadTime = DateTimeOffset.UtcNow,
            };
        }

        private sealed class ProgressAdapter(UploadProgressHandler handler) : IProgress<long>
        {
            public void Report(long value) => handler(value);
        }
    }
}
