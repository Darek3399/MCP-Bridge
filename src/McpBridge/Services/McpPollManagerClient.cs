using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace McpBridge.Services;

internal sealed class McpPollManagerClient
{
    private const string BaseUrl = "http://127.0.0.1:8001";
    private const string StateUrl = BaseUrl + "/__manager/state";
    private const string ServerCacheUrl = BaseUrl + "/__manager/server";
    private const string EventsUrl = BaseUrl + "/__manager/events";
    private const string RequestVerificationUrl = BaseUrl + "/__manager/verification/request";
    private const string VerifyUrl = BaseUrl + "/__manager/verification/subscribe";
    private const string VerificationCacheUrl = BaseUrl + "/__manager/verification/cache";
    private const string PromotionUrl = BaseUrl + "/__manager/verification/promote";
    private readonly string _managerApiKey;
    public McpPollManagerClient(string managerApiKey)
    {
        if (string.IsNullOrWhiteSpace(managerApiKey))
            throw new ArgumentException("A manager API key is required.", nameof(managerApiKey));
        _managerApiKey = managerApiKey;
    }

    private HttpWebRequest CreateManagerRequest(string url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Headers["X-McpBridge-Manager-Key"] = _managerApiKey;
        return request;
    }

    public async Task<PollManagerStateEnvelope> GetStateAsync()
    {
        var request = CreateManagerRequest(StateUrl);
        request.Method = "GET";
        request.Proxy = null;
        request.Timeout = 3000;
        request.ReadWriteTimeout = 3000;
        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        using var stream = response.GetResponseStream();
        if (stream == null) throw new InvalidOperationException("Poll Manager returned an empty state response.");
        var serializer = new DataContractJsonSerializer(typeof(PollManagerStateEnvelope));
        return serializer.ReadObject(stream) as PollManagerStateEnvelope
            ?? throw new InvalidOperationException("Poll Manager returned an invalid state response.");
    }

    public async Task SubscribeToStateChangesAsync(Func<Task> onChanged, CancellationToken cancellationToken)
    {
        var failureLogged = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpWebRequest? request = null;
            try
            {
                request = CreateManagerRequest(EventsUrl);
                request.Method = "GET";
                request.Accept = "text/event-stream";
                request.Proxy = null;
                request.Timeout = Timeout.Infinite;
                request.ReadWriteTimeout = Timeout.Infinite;
                using var registration = cancellationToken.Register(() =>
                {
                    try { request?.Abort(); } catch { }
                });
                using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
                using var stream = response.GetResponseStream();
                if (stream == null) throw new InvalidOperationException("Poll Manager event stream is empty.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await onChanged().ConfigureAwait(false);
                failureLogged = false;

                using var refreshSignal = new SemaphoreSlim(0, 1);
                using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var refreshTask = Task.Run(async () =>
                {
                    var refreshFailureLogged = false;
                    while (!refreshCancellation.IsCancellationRequested)
                    {
                        try
                        {
                            await refreshSignal.WaitAsync(refreshCancellation.Token).ConfigureAwait(false);
                            if (refreshCancellation.IsCancellationRequested) break;

                            await onChanged().ConfigureAwait(false);
                            refreshFailureLogged = false;

                            // Coalesce bursts of start/completion events from many
                            // independent workers into a bounded UI refresh rate.
                            await Task.Delay(100, refreshCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception refreshException)
                        {
                            if (!refreshFailureLogged)
                            {
                                ErrorLog.Write("McpPollManagerClient.CoalescedStateRefresh", refreshException);
                                refreshFailureLogged = true;
                            }
                        }
                    }
                }, CancellationToken.None);

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }
                        if (line.StartsWith("event:", StringComparison.Ordinal)) continue;
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                        try { refreshSignal.Release(); }
                        catch (SemaphoreFullException) { }
                    }
                }
                finally
                {
                    refreshCancellation.Cancel();
                    try { await refreshTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            catch (WebException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!failureLogged)
                {
                    ErrorLog.Write("McpPollManagerClient.SubscribeToStateChangesAsync", exception);
                    failureLogged = true;
                }
            }
            finally
            {
                try { request?.Abort(); } catch { }
            }

            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<PollManagerVerificationResponse> RequestVerificationAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        PollManagerVerificationEvent? completion = null;
        var request = CreateManagerRequest(RequestVerificationUrl);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "text/event-stream";
        request.Proxy = null;
        request.Timeout = 300000;
        request.ReadWriteTimeout = 300000;

        var payload = new PollManagerExistingVerificationRequest { ServerId = serverId };
        var serializer = new DataContractJsonSerializer(typeof(PollManagerExistingVerificationRequest));
        byte[] body;
        using (var memory = new MemoryStream())
        {
            serializer.WriteObject(memory, payload);
            body = memory.ToArray();
        }
        request.ContentLength = body.Length;

        using var registration = cancellationToken.Register(() =>
        {
            try { request.Abort(); } catch { }
        });
        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            await requestStream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);

        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
        using (var responseStream = response.GetResponseStream())
        {
            if (responseStream == null)
                throw new InvalidOperationException("Poll Manager verification stream is empty.");

            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var json = line.Substring(5).Trim();
                using var memory = new MemoryStream(Encoding.UTF8.GetBytes(json));
                var eventSerializer = new DataContractJsonSerializer(typeof(PollManagerVerificationEvent));
                completion = eventSerializer.ReadObject(memory) as PollManagerVerificationEvent;
                if (completion != null) break;
            }
        }

        if (completion == null)
            throw new EndOfStreamException("Poll Manager closed the verification subscription without a completion event.");
        if (completion.ConfigRevision <= 0 && !string.IsNullOrWhiteSpace(completion.Error))
            throw new InvalidOperationException(completion.Error);

        var cached = await ReadServerCacheAsync(serverId).ConfigureAwait(false);
        if (cached.State == null || cached.State.ConfigRevision != completion.ConfigRevision)
            throw new InvalidOperationException(
                "The MCP server configuration changed while the verification result was being read. Subscribe to the current worker again.");

        return new PollManagerVerificationResponse
        {
            Success = completion.Success,
            ServerId = completion.ServerId ?? serverId,
            Error = completion.Error ?? cached.State?.LastError,
            State = cached.State,
            DiscoveredServerName = cached.DiscoveredServerName,
            ToolNames = cached.ToolNames
        };
    }

    public async Task<PollManagerVerificationResponse> VerifyTemporaryAsync(
        PollManagerVerificationServer server,
        bool preserveOnSuccess = false,
        CancellationToken cancellationToken = default)
    {
        PollManagerVerificationEvent? completion = null;
        var cacheReadSucceeded = false;
        try
        {
            var request = CreateManagerRequest(VerifyUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "text/event-stream";
            request.Proxy = null;
            request.Timeout = 300000;
            request.ReadWriteTimeout = 300000;

            var payload = new PollManagerVerificationRequest { Server = server };
            var serializer = new DataContractJsonSerializer(
                typeof(PollManagerVerificationRequest),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            byte[] body;
            using (var memory = new MemoryStream())
            {
                serializer.WriteObject(memory, payload);
                body = memory.ToArray();
            }
            request.ContentLength = body.Length;

            using var registration = cancellationToken.Register(() =>
            {
                try { request.Abort(); } catch { }
            });
            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                await requestStream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);

            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            using (var responseStream = response.GetResponseStream())
            {
                if (responseStream == null)
                    throw new InvalidOperationException("Poll Manager verification stream is empty.");

                using var reader = new StreamReader(responseStream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var json = line.Substring(5).Trim();
                    using var memory = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    var eventSerializer = new DataContractJsonSerializer(typeof(PollManagerVerificationEvent));
                    completion = eventSerializer.ReadObject(memory) as PollManagerVerificationEvent;
                    if (completion != null) break;
                }
            }

            if (completion == null)
                throw new EndOfStreamException("Poll Manager closed the verification subscription without a completion event.");
            if (completion.ConfigRevision <= 0 && !string.IsNullOrWhiteSpace(completion.Error))
                throw new InvalidOperationException(completion.Error);

            PollManagerVerificationCache? cached = null;
            string? cacheReadError = null;
            try
            {
                cached = await ReadTemporaryVerificationCacheAsync(server.Id).ConfigureAwait(false);
                if (cached.State == null || cached.State.ConfigRevision != completion.ConfigRevision)
                    throw new InvalidOperationException(
                        "The temporary MCP configuration changed while its verification result was being read.");
                cacheReadSucceeded = true;
            }
            catch (Exception exception)
            {
                cacheReadError = exception.Message;
                if (completion.Success)
                    throw new InvalidOperationException("Poll Manager completed verification but its cache entry could not be read.", exception);
            }

            return new PollManagerVerificationResponse
            {
                Success = completion.Success,
                ServerId = completion.ServerId ?? server.Id,
                Error = completion.Error ?? cached?.State?.LastError ?? cacheReadError,
                State = cached?.State,
                DiscoveredServerName = cached?.DiscoveredServerName,
                ToolNames = cached?.ToolNames ?? new List<string>()
            };
        }
        finally
        {
            if (!preserveOnSuccess || !cacheReadSucceeded)
            {
                try { await RemoveTemporaryVerificationAsync(server.Id).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    ErrorLog.Write("McpPollManagerClient.RemoveTemporaryVerificationAsync", exception);
                }
            }
        }
    }

    public async Task<PollManagerPromotionResponse> PromoteTemporaryVerificationAsync(
        string temporaryServerId,
        PollManagerVerificationServer permanentServer)
    {
        var request = CreateManagerRequest(PromotionUrl);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Proxy = null;
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;

        var payload = new PollManagerVerificationPromotionRequest
        {
            TemporaryServerId = temporaryServerId,
            Server = permanentServer
        };
        var serializer = new DataContractJsonSerializer(
            typeof(PollManagerVerificationPromotionRequest),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        byte[] body;
        using (var memory = new MemoryStream())
        {
            serializer.WriteObject(memory, payload);
            body = memory.ToArray();
        }
        request.ContentLength = body.Length;
        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            await requestStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);

        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        using var stream = response.GetResponseStream();
        if (stream == null) throw new InvalidOperationException("Poll Manager promotion response is empty.");
        var resultSerializer = new DataContractJsonSerializer(typeof(PollManagerPromotionResponse));
        return resultSerializer.ReadObject(stream) as PollManagerPromotionResponse
            ?? throw new InvalidOperationException("Poll Manager returned an invalid promotion response.");
    }

    public async Task RemoveTemporaryVerificationAsync(string serverId)
    {
        await RemoveTemporaryVerificationCacheAsync(serverId).ConfigureAwait(false);
    }

    private async Task<PollManagerVerificationCache> ReadTemporaryVerificationCacheAsync(string serverId)
    {
        var url = VerificationCacheUrl + "?id=" + Uri.EscapeDataString(serverId);
        return await ReadCacheAsync(url).ConfigureAwait(false);
    }

    private async Task<PollManagerVerificationCache> ReadServerCacheAsync(string serverId)
    {
        var url = ServerCacheUrl + "?id=" + Uri.EscapeDataString(serverId);
        return await ReadCacheAsync(url).ConfigureAwait(false);
    }

    private async Task<PollManagerVerificationCache> ReadCacheAsync(string url)
    {
        var request = CreateManagerRequest(url);
        request.Method = "GET";
        request.Proxy = null;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;

        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        using var stream = response.GetResponseStream();
        if (stream == null) throw new InvalidOperationException("Poll Manager verification cache response is empty.");
        var serializer = new DataContractJsonSerializer(typeof(PollManagerVerificationCache));
        return serializer.ReadObject(stream) as PollManagerVerificationCache
            ?? throw new InvalidOperationException("Poll Manager returned an invalid temporary cache entry.");
    }

    private async Task RemoveTemporaryVerificationCacheAsync(string serverId)
    {
        var url = VerificationCacheUrl + "?id=" + Uri.EscapeDataString(serverId);
        var request = CreateManagerRequest(url);
        request.Method = "DELETE";
        request.Proxy = null;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
    }
}

[DataContract]
internal sealed class PollManagerStateEnvelope
{
    [DataMember(Name = "servers")]
    public List<PollManagerServerState> Servers { get; set; } = new();
}

[DataContract]
internal sealed class PollManagerServerState
{
    [DataMember(Name = "serverId")]
    public string ServerId { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "enabled")]
    public bool Enabled { get; set; }

    [DataMember(Name = "pending")]
    public bool Pending { get; set; }

    [DataMember(Name = "scope")]
    public string Scope { get; set; } = "local";

    [DataMember(Name = "transport")]
    public string Transport { get; set; } = "http";

    [DataMember(Name = "health")]
    public string Health { get; set; } = "waiting";

    [DataMember(Name = "isPolling")]
    public bool IsPolling { get; set; }

    [DataMember(Name = "pollIntervalMs")]
    public int PollIntervalMs { get; set; }

    [DataMember(Name = "configRevision")]
    public int ConfigRevision { get; set; }

    [DataMember(Name = "serverRevision")]
    public int ServerRevision { get; set; }

    [DataMember(Name = "catalogRevision")]
    public int CatalogRevision { get; set; }

    [DataMember(Name = "lastAttemptAt")]
    public string? LastAttemptAt { get; set; }

    [DataMember(Name = "lastCompletedAt")]
    public string? LastCompletedAt { get; set; }

    [DataMember(Name = "lastSuccessAt")]
    public string? LastSuccessAt { get; set; }

    [DataMember(Name = "responseTimeMs")]
    public int? ResponseTimeMs { get; set; }

    [DataMember(Name = "consecutiveFailures")]
    public int ConsecutiveFailures { get; set; }

    [DataMember(Name = "stale")]
    public bool Stale { get; set; }

    [DataMember(Name = "lastError")]
    public string? LastError { get; set; }

    [DataMember(Name = "toolsCount")]
    public int ToolsCount { get; set; }

    [DataMember(Name = "toolsPreview")]
    public List<string> ToolsPreview { get; set; } = new();

    [DataMember(Name = "updatedAt")]
    public string? UpdatedAt { get; set; }
}

[DataContract]
internal sealed class PollManagerExistingVerificationRequest
{
    [DataMember(Name = "serverId")]
    public string ServerId { get; set; } = string.Empty;
}

[DataContract]
internal sealed class PollManagerVerificationRequest
{
    [DataMember(Name = "server")]
    public PollManagerVerificationServer Server { get; set; } = new();
}

[DataContract]
internal sealed class PollManagerVerificationServer
{
    [DataMember(Name = "id")]
    public string Id { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "url")]
    public string Url { get; set; } = string.Empty;

    [DataMember(Name = "headers", EmitDefaultValue = false)]
    public Dictionary<string, string>? Headers { get; set; }

    [DataMember(Name = "enabled")]
    public bool Enabled { get; set; } = true;

    [DataMember(Name = "scope")]
    public string Scope { get; set; } = "local";

    [DataMember(Name = "transport")]
    public string Transport { get; set; } = "http";

    [DataMember(Name = "configurationFingerprint", EmitDefaultValue = false)]
    public string? ConfigurationFingerprint { get; set; }
}

[DataContract]
internal sealed class PollManagerVerificationPromotionRequest
{
    [DataMember(Name = "temporaryServerId")]
    public string TemporaryServerId { get; set; } = string.Empty;

    [DataMember(Name = "server")]
    public PollManagerVerificationServer Server { get; set; } = new();
}

[DataContract]
internal sealed class PollManagerPromotionResponse
{
    [DataMember(Name = "promoted")]
    public bool Promoted { get; set; }

    [DataMember(Name = "serverId")]
    public string? ServerId { get; set; }

    [DataMember(Name = "configRevision")]
    public int ConfigRevision { get; set; }

    [DataMember(Name = "cacheRevision")]
    public string? CacheRevision { get; set; }

    [DataMember(Name = "completedAt")]
    public string? CompletedAt { get; set; }

    [DataMember(Name = "error")]
    public string? Error { get; set; }
}

[DataContract]
internal sealed class PollManagerVerificationEvent
{
    [DataMember(Name = "success")]
    public bool Success { get; set; }

    [DataMember(Name = "serverId")]
    public string? ServerId { get; set; }

    [DataMember(Name = "runId")]
    public string? RunId { get; set; }

    [DataMember(Name = "configRevision")]
    public int ConfigRevision { get; set; }

    [DataMember(Name = "cacheRevision")]
    public string? CacheRevision { get; set; }

    [DataMember(Name = "completedAt")]
    public string? CompletedAt { get; set; }

    [DataMember(Name = "error")]
    public string? Error { get; set; }
}

[DataContract]
internal sealed class PollManagerVerificationCache
{
    [DataMember(Name = "state")]
    public PollManagerServerState? State { get; set; }

    [DataMember(Name = "toolNames")]
    public List<string> ToolNames { get; set; } = new();

    [DataMember(Name = "discoveredServerName")]
    public string? DiscoveredServerName { get; set; }
}

internal sealed class PollManagerVerificationResponse
{
    public bool Success { get; set; }
    public string? ServerId { get; set; }
    public string? Error { get; set; }
    public PollManagerServerState? State { get; set; }
    public List<string> ToolNames { get; set; } = new();
    public string? DiscoveredServerName { get; set; }
}
