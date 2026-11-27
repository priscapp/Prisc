using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Handlers;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Prisc.Upload
{
    public class CatboxUploadProvider : UploadProviderBase
    {
        public override string Name => "catbox.moe";
        public override string Description => "Anonymous file host with optional expiry";
        public override SupportedUploadType SupportedUpload => SupportedUploadType.All;
        public override Stream Icon => new Resource().CatboxIcon;

        [Description("Never uploads to catbox.moe, up to 200 MB. Any other expiry uses litterbox.catbox.moe, up to 1 GB.")]
        public CatBoxExpiry ExpireUploads
        {
            get => _expireUploads;
            set => Set(ref _expireUploads, value);
        }

        [Description("Optional. Your catbox.moe account's userhash (catbox.moe > Manage account). Catbox refuses anonymous uploads from some networks (VPNs, datacenter IPs); uploading with a userhash avoids that. Only used when expiry is Never.")]
        public string Userhash
        {
            get => _userhash;
            set => Set(ref _userhash, value);
        }

        private CatBoxExpiry _expireUploads;
        private string _userhash;

        public override async Task<UploadResult> UploadAsync(Stream fileStream, UploadProgressHandler progress, string uploadName, CancellationToken cancelToken)
        {
            // for expiry = never, we use the 'catbox' api (which never expires).
            // for expiry < never, we use the 'litterbox' api, which always expires.
            
            var url = "https://litterbox.catbox.moe/resources/internals/api.php";
            
            Dictionary<string, string> args = new()
            {
                { "reqtype", "fileupload" }
            };

            switch (ExpireUploads)
            {
                case CatBoxExpiry.Never:
                    url = "https://catbox.moe/user/api.php";
                    if (!String.IsNullOrWhiteSpace(Userhash))
                        args.Add("userhash", Userhash.Trim());
                    break;
                case CatBoxExpiry._1h:
                    args.Add("time", "1h");
                    break;
                case CatBoxExpiry._12h:
                    args.Add("time", "12h");
                    break;
                case CatBoxExpiry._24h:
                    args.Add("time", "24h");
                    break;
                case CatBoxExpiry._72h:
                    args.Add("time", "72h");
                    break;
            }

            // the whole transfer runs inside one request, so a fixed 100 s HttpClient timeout fails
            // any recording that takes longer than that to send. Allow a floor of 64 KB/s instead;
            // the user can still cancel at any time.
            var timeout = TimeSpan.FromSeconds(100);
            if (fileStream.CanSeek)
                timeout += TimeSpan.FromSeconds((fileStream.Length - fileStream.Position) / (64 * 1024));

            // catbox intermittently answers a successful upload of a larger file with an empty 200
            // (observed Nov 2026: ~20 MB+ uploads, a few seconds to a minute long). The file is stored
            // anyway, and catbox de-duplicates by content, so sending the same bytes again returns
            // the existing link. Retry once on an empty body before giving up.
            long start = fileStream.CanSeek ? fileStream.Position : -1;
            string resp = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    if (start < 0)
                        break;
                    fileStream.Position = start;
                }

                // StreamContent disposes its stream with the request; keep it open for the retry.
                try
                {
                    resp = (await SendFileAsFormData(url, new LeaveOpenStream(fileStream), "fileToUpload", progress, uploadName,
                        args, timeout: timeout, cancelToken: cancelToken)).Trim();
                }
                catch (Exception ex) when (IsInvalidUploader(ex.Message))
                {
                    throw new Exception(InvalidUploaderMessage(), ex);
                }
                catch (Exception ex) when (ex.Message.Contains("(HTML error page", StringComparison.Ordinal))
                {
                    // the site's WAF (BunkerWeb) answers with an error page rather than the API's text:
                    // a 500 when it chokes on the upload, a 403 once it has banned the client IP.
                    var page = ex.Message.Substring(ex.Message.IndexOf("(HTML error page", StringComparison.Ordinal));
                    throw new Exception($"{new Uri(url).Host}'s firewall rejected the upload {page}. " +
                        "The service may be having problems, or may be temporarily blocking this network. Try again later.", ex);
                }
                catch (Exception ex) when (ex.Message.Contains("Not signed in", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("catbox.moe didn't recognise the userhash in the uploader settings. " +
                        "Copy it again from catbox.moe > Manage account, or clear it to upload anonymously.", ex);
                }

                if (resp.Length > 0)
                    break;
            }

            if (IsInvalidUploader(resp))
                throw new Exception(InvalidUploaderMessage());

            if (!Uri.TryCreate(resp, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
            {
                throw new Exception(resp.Length == 0
                    ? $"{new Uri(url).Host} accepted the upload but returned no link."
                    : $"{new Uri(url).Host} rejected the upload: {SummarizeErrorBody(resp)}");
            }

            return new UploadResult()
            {
                Provider = this,
                PublicUrl = resp,
            };
        }

        // catbox and litterbox answer "Invalid uploader" (HTTP 412, sometimes 200) when they filter
        // anonymous uploads from the client's network.
        private static bool IsInvalidUploader(string text)
            => text?.Contains("Invalid uploader", StringComparison.OrdinalIgnoreCase) == true;

        private string InvalidUploaderMessage() => ExpireUploads == CatBoxExpiry.Never
            ? String.IsNullOrWhiteSpace(Userhash)
                ? "catbox.moe refused an anonymous upload from this network (\"Invalid uploader\"). This happens on VPNs and datacenter IPs. " +
                  "Add your catbox.moe account userhash in the uploader settings, or upload from another network."
                : "catbox.moe refused the upload (\"Invalid uploader\"). Check that the userhash in the uploader settings is correct."
            : "litterbox.catbox.moe refused an upload from this network (\"Invalid uploader\"). This happens on VPNs and datacenter IPs, " +
              "and litterbox has no accounts to get around it. Upload from another network, or set expiry to Never and add a catbox.moe userhash.";

        private sealed class LeaveOpenStream(Stream inner) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => inner.Read(buffer);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        public enum CatBoxExpiry
        {
            Never = 0,
            [Description("1 hour")]
            _1h = 1,
            [Description("12 hours")]
            _12h = 2,
            [Description("1 day")]
            _24h = 3,
            [Description("3 days")]
            _72h = 4,
        }
    }
}
