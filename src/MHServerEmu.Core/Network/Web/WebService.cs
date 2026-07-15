using System;
using System.Diagnostics;
using System.Net;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Core.Network.Web
{
    public class WebService
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Dictionary<string, WebHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

        private HttpListener _listener;
        private CancellationTokenSource _cts;

        public WebServiceSettings Settings { get; }
        public bool IsRunning { get; private set; }

        public int HandlerCount { get => _handlers.Count; }
        public int HandledRequests { get; private set; }

        public WebService(WebServiceSettings settings)
        {
            Settings = settings;
        }

        public override string ToString()
        {
            return Settings.Name;
        }

        /// <summary>
        /// Starts the web service. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool Start()
        {
            if (IsRunning)
                return false;

            Debug.Assert(_listener == null);
            Debug.Assert(_cts == null);

            string url = Settings.ListenUrl;

            _listener = new();
            _listener.Prefixes.Add(url);

            // HttpListener (backed by Windows' http.sys) matches incoming
            // requests against registered prefixes using an EXACT Host
            // header comparison — a prefix bound to the literal "localhost"
            // only accepts requests whose Host header is "localhost:port".
            // Any client whose HTTP stack resolves the hostname to a numeric
            // loopback address first and sends THAT as the Host header
            // (127.0.0.1 or ::1) gets rejected with 400 Bad Request, even
            // though it's the same machine on the same port. The 2013-era
            // Unreal Engine 3 client does exactly this, which surfaced as
            // "Site Config Not Available" even with the server up and
            // otherwise reachable via curl/tools that preserve "localhost"
            // verbatim. Register the loopback address forms too so every
            // client construction style is accepted. Scoped to only fire
            // when the configured host is "localhost" — a server explicitly
            // bound to a real LAN/public address is left untouched.
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUrl) &&
                parsedUrl.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                TryAddLoopbackAliasPrefix($"http://127.0.0.1:{parsedUrl.Port}{parsedUrl.AbsolutePath}");
                TryAddLoopbackAliasPrefix($"http://[::1]:{parsedUrl.Port}{parsedUrl.AbsolutePath}");
            }

            _listener.Start();

            _cts = new();
            Task.Run(HandleRequestsAsync);

            IsRunning = true;
            return true;
        }

        /// <summary>
        /// Adds an extra prefix to the listener (a loopback address alias for
        /// the configured "localhost" prefix) without failing Start() if the
        /// OS rejects it for any reason — the primary "localhost" prefix
        /// already succeeded, so a failed alias just means that particular
        /// address form won't be accepted; not fatal.
        /// </summary>
        private void TryAddLoopbackAliasPrefix(string prefix)
        {
            try
            {
                _listener.Prefixes.Add(prefix);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Start(): failed to register loopback alias prefix {prefix}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the currently running REST service. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool Stop()
        {
            if (IsRunning == false)
                return false;

            Debug.Assert(_listener != null);
            Debug.Assert(_cts != null);

            _cts.Cancel();
            _cts.Dispose();
            _cts = null;

            _listener.Stop();
            _listener = null;

            IsRunning = false;
            return true;
        }

        /// <summary>
        /// Returns the currently registered <see cref="WebHandler"/> for the specified local path if available.
        /// Returns the fallback handler if no handler is registered for the local path, which may be <see langword="null"/>.
        /// </summary>
        public WebHandler GetHandler(string localPath)
        {
            if (_handlers.TryGetValue(localPath, out WebHandler handler) == false)
                return Settings.FallbackHandler;

            return handler;
        }

        /// <summary>
        /// Registers the provided <see cref="WebHandler"/> for the specified local path.
        /// Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RegisterHandler(string localPath, WebHandler handler)
        {
            bool added = _handlers.TryAdd(localPath, handler);

            if (added)
                handler.Register(this, localPath);
            else
                Logger.Warn($"RegisterHandler(): Local path {localPath} already has a registered handler");

            return added;
        }

        /// <summary>
        /// Removed the currently registered <see cref="WebHandler"/> for the specified local path.
        /// Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RemoveHandler(string localPath)
        {
            bool removed = _handlers.Remove(localPath, out WebHandler handler);

            if (removed)
                handler.Unregister();
            else
                Logger.Warn($"RemoveHandler(): No handler is registered for local path {localPath}");

            return removed;
        }

        /// <summary>
        /// Handles incoming requests asynchronously.
        /// </summary>
        private async Task HandleRequestsAsync()
        {
            Logger.Info($"{this} is listening on {Settings.ListenUrl}...");

            while (_cts.IsCancellationRequested == false)
            {
                try
                {
                    HttpListenerContext httpContext = await _listener.GetContextAsync().WaitAsync(_cts.Token);

                    WebRequestContext requestContext = new(httpContext);

                    // This may be either a registered handler or a fallback handler.
                    WebHandler handler = GetHandler(requestContext.LocalPath);
                    await handler?.HandleAsync(requestContext);

                    // The client may have disconnected mid-response (browser
                    // reload, app poll timed out, etc.). Closing a response
                    // for a dead connection throws HttpListenerException 1229
                    // (ERROR_OPERATION_ABORTED). It's not a listener failure
                    // — swallow it and keep serving.
                    try { httpContext.Response.Close(); }
                    catch (HttpListenerException) { }
                    catch (ObjectDisposedException) { }

                    HandledRequests++;
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (HttpListenerException hle)
                {
                    // Same client-disconnect scenario during
                    // GetContextAsync / early read — the SDK surfaces these
                    // as HttpListenerException. Don't tear down the listener.
                    // The old behaviour turned a single dropped browser tab
                    // or app-poll timeout into a permanent server outage.
                    Logger.Warn($"HandleRequestAsync(): client-side error {hle.ErrorCode}: {hle.Message}");
                }
                catch (Exception e)
                {
                    // NOTE: HandleRequest() should catch and handle exceptions when processing requests.
                    // If we got to this part, something must be wrong with the listener.
                    Logger.Error($"HandleRequestAsync(): {e}");
                    return;
                }
            }
        }
    }
}
