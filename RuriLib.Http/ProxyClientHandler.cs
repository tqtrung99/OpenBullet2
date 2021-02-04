﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using RuriLib.Proxies;
using System.Text;
using RuriLib.Proxies.Exceptions;
using System.Collections.Generic;

namespace RuriLib.Http
{
    /// <summary>
    /// Represents <see cref="HttpMessageHandler"/> with <see cref="IProxyClient{T}"/>
    /// to provide the <see cref="HttpClient"/> support for <see cref="IProxy"/> proxy type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ProxyClientHandler : HttpMessageHandler, IDisposable
    {
        private readonly ProxyClient proxyClient;
        
        private Stream connectionCommonStream;
        private NetworkStream connectionNetworkStream;

        #region Properties
        /// <summary>
        /// The underlying proxy client.
        /// </summary>
        public ProxyClient ProxyClient => proxyClient;

        /// <summary>
        /// Gets the raw bytes of the last request that was sent.
        /// </summary>
        public List<byte[]> RawRequests { get; } = new();

        /// <summary>
        /// Allow automatic redirection on 3xx reply.
        /// </summary>
        public bool AllowAutoRedirect { get; set; } = true;

        /// <summary>
        /// The allowed SSL or TLS protocols.
        /// </summary>
        public SslProtocols SslProtocols { get; set; } = SslProtocols.None;

        /// <summary>
        /// If true, <see cref="AllowedCipherSuites"/> will be used instead of the default ones.
        /// </summary>
        public bool UseCustomCipherSuites { get; set; } = false;

        /// <summary>
        /// The cipher suites to send to the server during the TLS handshake, in order.
        /// The default value of this property contains the cipher suites sent by Firefox as of 21 Dec 2020.
        /// </summary>
        public TlsCipherSuite[] AllowedCipherSuites { get; set; } = new TlsCipherSuite[]
        {
            TlsCipherSuite.TLS_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
            TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
            TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
            TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
            TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
            TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
            TlsCipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA
        };

        /// <summary>
        /// Gets the type of decompression method used by the handler for automatic 
        /// decompression of the HTTP content response.
        /// </summary>
        /// <remarks>
        /// Support GZip and Deflate encoding automatically
        /// </remarks>
        public DecompressionMethods AutomaticDecompression => DecompressionMethods.GZip | DecompressionMethods.Deflate;

        /// <summary>
        /// Gets or sets a value that indicates whether the handler uses the CookieContainer
        /// property to store server cookies and uses these cookies when sending requests.
        /// </summary>
        public bool UseCookies { get; set; } = true;

        /// <summary>
        /// Gets or sets the cookie container used to store server cookies by the handler.
        /// </summary>
        public CookieContainer CookieContainer { get; set; }

        /// <summary>
        /// Gets or sets delegate to verifies the remote Secure Sockets Layer (SSL) 
        /// certificate used for authentication.
        /// </summary>
        public RemoteCertificateValidationCallback ServerCertificateCustomValidationCallback { get; set; }

        /// <summary>
        /// Gets or sets the X509 certificate revocation mode.
        /// </summary>
        public X509RevocationMode CertRevocationMode { get; set; }
        #endregion

        /// <summary>
        /// Creates a new instance of <see cref="ProxyClientHandler"/> given a <paramref name="proxyClient"/>.
        /// </summary>
        public ProxyClientHandler(ProxyClient proxyClient)
        {
            this.proxyClient = proxyClient ?? throw new ArgumentNullException(nameof(proxyClient));
        }

        /// <summary>
        /// Asynchronously sends a <paramref name="request"/> and returns an <see cref="HttpResponseMessage"/>.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
        protected async override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (UseCookies && CookieContainer == null)
            {
                throw new ArgumentNullException(nameof(CookieContainer));
            }

            await CreateConnection(request, cancellationToken).ConfigureAwait(false);
            await SendDataAsync(request, cancellationToken).ConfigureAwait(false);
            var responseMessage = await ReceiveDataAsync(request, cancellationToken).ConfigureAwait(false);

            // Optionally perform auto redirection on 3xx response
            if (((int)responseMessage.StatusCode) / 100 == 3 && AllowAutoRedirect)
            {
                // Compute the redirection URI
                var redirectUri = responseMessage.Headers.Location.IsAbsoluteUri
                    ? responseMessage.Headers.Location
                    : new Uri(request.RequestUri, responseMessage.Headers.Location);

                // If not 307, change the method to GET
                if (responseMessage.StatusCode != HttpStatusCode.RedirectKeepVerb)
                {
                    request.Method = HttpMethod.Get;
                    request.Content = null;
                }

                // Port over the cookies if the domains are different
                if (request.RequestUri.Host != redirectUri.Host)
                {
                    var cookies = CookieContainer.GetCookies(request.RequestUri);
                    foreach (Cookie cookie in cookies)
                    {
                        CookieContainer.Add(redirectUri, new Cookie(cookie.Name, cookie.Value));
                    }
                }

                // Set the new URI
                request.RequestUri = redirectUri;

                // Perform a new request
                return await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            return responseMessage;
        }

        private async Task SendDataAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            byte[] buffer;
            using var ms = new MemoryStream();

            // Send the first line
            buffer = Encoding.ASCII.GetBytes(RequestBuilder.BuildFirstLine(request));
            ms.Write(buffer);
            await connectionCommonStream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

            // Send the headers
            buffer = Encoding.ASCII.GetBytes(RequestBuilder.BuildHeaders(request, CookieContainer));
            ms.Write(buffer);
            await connectionCommonStream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

            // Optionally send the content
            if (request.Content != null)
            {
                buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                ms.Write(buffer);
                await connectionCommonStream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            }

            ms.Seek(0, SeekOrigin.Begin);
            RawRequests.Add(ms.ToArray());
        }

        private async Task<HttpResponseMessage> ReceiveDataAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var responseBuilder = new ResponseBuilder(1024, CookieContainer, request.RequestUri);
            return await responseBuilder.GetResponseAsync(request, connectionCommonStream, cancellationToken);
        }

        private async Task CreateConnection(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Get the stream from the proxies TcpClient
            var uri = request.RequestUri;
            var tcpClient = await proxyClient.ConnectAsync(uri.Host, uri.Port, null, cancellationToken);
            connectionNetworkStream = tcpClient.GetStream();

            // If https, set up a TLS stream
            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    SslStream sslStream;
                    sslStream = new SslStream(connectionNetworkStream, false, ServerCertificateCustomValidationCallback);

                    var sslOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                        EnabledSslProtocols = SslProtocols,
                        CertificateRevocationCheckMode = CertRevocationMode
                    };

                    if (UseCustomCipherSuites)
                        sslOptions.CipherSuitesPolicy = new CipherSuitesPolicy(AllowedCipherSuites);

                    await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);
                    connectionCommonStream = sslStream;
                }
                catch (Exception ex)
                {
                    if (ex is IOException || ex is AuthenticationException)
                    {
                        throw new ProxyException("Failed SSL connect");
                    }

                    throw;
                }
            }
            else
            {
                connectionCommonStream = connectionNetworkStream;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connectionCommonStream?.Dispose();
                connectionNetworkStream?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}