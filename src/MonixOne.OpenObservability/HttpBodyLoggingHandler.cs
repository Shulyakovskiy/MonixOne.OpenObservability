using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using Refit;

namespace MonixOne.OpenObservability;

/// <summary>
/// Connects the library's passive stream monitoring to HttpClient content.
/// Captures only bytes sent or consumed by the caller, with no extra reads or timers.
/// </summary>
internal sealed class HttpBodyLoggingHandler(ILogger<HttpBodyLoggingHandler> logger, HttpBodySanitizer sanitizer)
    : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<Activity> ActivityKey =
        new("MonixOne.OpenObservability.Activity");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Content is { } content)
            request.Content = Wrap(content, request, response: null);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = Wrap(response.Content, request, response);
        return response;
    }

    private HttpContent Wrap(HttpContent content, HttpRequestMessage request, HttpResponseMessage? response)
    {
        var responseBody = response is not null;
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("http_method", request.Method.Method),
            new("server_address", request.RequestUri?.Host),
            new("http_status_code", response is null ? null : (int)response.StatusCode)
        };
        fields.AddRange(ServiceHttpClientLogEnricher.CreateRequestAttributes(request));
        if (response is not null)
            fields.Add(new("upstream_status_code", (int)response.StatusCode));
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>(HttpRequestMessageOptions.RelativePathTemplate), out var route))
            fields.Add(new("http_route", route));
        return new MonitoredContent(content,
            new BodyCapture(logger, sanitizer, content.Headers, request, responseBody, fields));
    }

    /// <summary>
    /// Preserves HttpContent headers, cancellation and stream ownership.
    /// The transport owns serialization targets; the response content owns read streams.
    /// </summary>
    private sealed class MonitoredContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly BodyCapture _capture;

        internal MonitoredContent(HttpContent inner, BodyCapture capture)
        {
            _inner = inner;
            _capture = capture;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var completed = false;
            try
            {
                // Do not dispose this monitor: its target belongs to the HTTP transport.
                // The library owns the stream implementation and reports successful writes.
                await _inner.CopyToAsync(_capture.Monitor(stream, reads: false), context, cancellationToken).ConfigureAwait(false);
                completed = true;
            }
            finally { _capture.Finish(completed); }
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var completed = false;
            try
            {
                _inner.CopyTo(_capture.Monitor(stream, reads: false), context, cancellationToken);
                completed = true;
            }
            finally { _capture.Finish(completed); }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            await CreateContentReadStreamAsync(CancellationToken.None).ConfigureAwait(false);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            _capture.Monitor(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), reads: true);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            _capture.Monitor(_inner.ReadAsStream(cancellationToken), reads: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _capture.Finish(completed: false);
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Retains at most the configured byte limit and emits one sanitized record.
    /// Partial reads, unsupported encodings and oversized bodies never expose a prefix.
    /// </summary>
    private sealed class BodyCapture
    {
        private readonly object _gate = new();
        private readonly ILogger _logger;
        private readonly HttpBodySanitizer _sanitizer;
        private readonly string? _contentType;
        private readonly long? _expectedLength;
        private readonly bool _response;
        private readonly List<KeyValuePair<string, object?>> _fields;
        private readonly byte[] _buffer;
        private readonly HttpRequestMessage _request;
        private readonly ActivityContext _origin;
        private readonly Activity? _originActivity;
        private readonly Encoding? _encoding;
        private string? _omission;
        private int _count;
        private bool _finished;

        internal BodyCapture(ILogger logger, HttpBodySanitizer sanitizer, HttpContentHeaders headers,
            HttpRequestMessage request, bool response, List<KeyValuePair<string, object?>> fields)
        {
            _logger = logger;
            _sanitizer = sanitizer;
            _contentType = headers.ContentType?.ToString();
            _expectedLength = headers.ContentLength;
            _response = response;
            _request = request;
            var current = Activity.Current?.Context ?? default;
            _origin = response ? ResolveContext(request, current) : current;
            _originActivity = response && request.Options.TryGetValue(ActivityKey, out var activity)
                ? activity : Activity.Current;
            _fields = fields;
            var mediaType = headers.ContentType?.MediaType;
            if (headers.ContentEncoding.Count > 0)
                _omission = "content_encoding";
            else if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                     || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
                     || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
                _omission = "unsupported_content_type";
            else
            {
                try
                { _encoding = Encoding.GetEncoding(headers.ContentType?.CharSet?.Trim('"') ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
                catch (ArgumentException) { _omission = "unsupported_encoding"; }
            }
            _buffer = _omission is null ? new byte[HttpLoggingConfiguration.BodyLimitBytes] : [];
        }

        private static ActivityContext ResolveContext(HttpRequestMessage request, ActivityContext fallback)
        {
            if (request.Options.TryGetValue(ActivityKey, out var activity))
                return activity.Context;
            if (fallback.TraceId != default)
                return fallback;
            return request.Headers.TryGetValues("traceparent", out var values)
                   && ActivityContext.TryParse(values.FirstOrDefault(), traceState: null, out var context)
                ? context : default;
        }

        internal Stream Monitor(Stream stream, bool reads)
        {
            var monitor = new MonitoringStream(stream);
            if (reads)
            {
                var readHasCapacity = false;
                monitor.WillReadAny += (_, buffer) => readHasCapacity = !buffer.IsEmpty;
                monitor.DidReadAny += (_, bytes) =>
                {
                    Append(bytes);
                    if (bytes.IsEmpty && readHasCapacity)
                        Finish(completed: true);
                };
                monitor.Disposed += (_, _) => Finish(completed: false);
                monitor.DidSeek += (_, _) => { lock (_gate) _omission = "non_sequential_read"; };
            }
            else
                monitor.DidWriteAny += (_, bytes) => Append(bytes);
            return monitor;
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                if (_finished || _omission is not null)
                    return;
                if (bytes.Length >= _buffer.Length - _count)
                {
                    _omission = "body_limit";
                    Array.Clear(_buffer);
                }
                else
                {
                    bytes.CopyTo(_buffer.AsSpan(_count));
                    _count += bytes.Length;
                }
            }
        }

        internal void Finish(bool completed)
        {
            lock (_gate)
            {
                if (_finished)
                    return;
                _finished = true;
                if (!completed && _expectedLength != _count)
                    _omission ??= "incomplete_body";
                string body;
                try
                {
                    body = _omission is null
                        ? _sanitizer.Sanitize(_encoding!.GetString(_buffer, 0, _count), _contentType)
                        : $"[omitted: {_omission}]";
                }
                catch (DecoderFallbackException) { body = "[omitted: invalid_encoding]"; }
                finally { Array.Clear(_buffer); }
                var origin = _response ? _origin : ResolveContext(_request, _origin);
                if (origin.TraceId != default)
                {
                    _fields.Add(new("trace_id", origin.TraceId.ToString()));
                    _fields.Add(new("span_id", origin.SpanId.ToString()));
                    _fields.Add(new("trace_flags", (int)origin.TraceFlags));
                }
                _fields.Add(new(_response ? "ResponseBody" : "RequestBody", body));
                _fields.Add(new(_response ? "http.response.header.content-type" : "http.request.header.content-type", _contentType));
                var eventId = new EventId(_response ? 1002 : 1001,
                    _response ? "http.client.response.body" : "http.client.request.body");
                var previousActivity = Activity.Current;
                Activity.Current = !_response && _request.Options.TryGetValue(ActivityKey, out var activity)
                    ? activity : _originActivity;
                try
                {
                    _logger.Log(LogLevel.Information, eventId, _fields, exception: null,
                        (fields, _) => string.Join(Environment.NewLine, fields.Select(field => $"{field.Key}: {field.Value}")));
                }
                finally { Activity.Current = previousActivity; }
            }
        }
    }
}
