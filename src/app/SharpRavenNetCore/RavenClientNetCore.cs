#region License

// Copyright (c) 2014 The Sentry Team and individual contributors.
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted
// provided that the following conditions are met:
// 
//     1. Redistributions of source code must retain the above copyright notice, this list of
//        conditions and the following disclaimer.
// 
//     2. Redistributions in binary form must reproduce the above copyright notice, this list of
//        conditions and the following disclaimer in the documentation and/or other materials
//        provided with the distribution.
// 
//     3. Neither the name of the Sentry nor the names of its contributors may be used to
//        endorse or promote products derived from this software without specific prior written
//        permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
// IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Newtonsoft.Json;

using SharpRavenNetCore.Data;
using SharpRavenNetCore.Logging;
using SharpRavenNetCore.Utilities;
using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Net;

namespace SharpRavenNetCore
{
    /// <summary>
    /// The Raven Client, responsible for capturing exceptions and sending them to Sentry.
    /// </summary>
    public partial class RavenClientNetCore : IRavenClient
    {
        private readonly Dsn currentDsn;
        private readonly IDictionary<string, string> defaultTags;
        private readonly IJsonPacketFactory jsonPacketFactory;
        private readonly ISentryRequestFactory sentryRequestFactory;
        private readonly ISentryUserFactory sentryUserFactory;
        private readonly IHttpContextAccessor contextAccessor;


        /// <summary>
        /// Initializes a new instance of the <see cref="RavenClientNetCore" /> class.
        /// </summary>
        /// <param name="dsn">The Data Source Name in Sentry.</param>
        /// <param name="jsonPacketFactory">The optional factory that will be used to create the <see cref="JsonPacket" /> that will be sent to Sentry.</param>
        /// <param name="sentryRequestFactory">The optional factory that will be used to create the <see cref="SentryRequest"/> that will be sent to Sentry.</param>
        /// <param name="sentryUserFactory">The optional factory that will be used to create the <see cref="SentryUser"/> that will be sent to Sentry.</param>
        public RavenClientNetCore(string dsn,
                           IHttpContextAccessor contextAccessor,
                           IJsonPacketFactory jsonPacketFactory = null,
                           ISentryRequestFactory sentryRequestFactory = null,
                           ISentryUserFactory sentryUserFactory = null)
            : this(new Dsn(dsn), contextAccessor, jsonPacketFactory, sentryRequestFactory, sentryUserFactory)
        {
            this.contextAccessor = contextAccessor;
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="RavenClientNetCore" /> class.
        /// </summary>
        /// <param name="dsn">The Data Source Name in Sentry.</param>
        /// <param name="jsonPacketFactory">The optional factory that will be used to create the <see cref="JsonPacket" /> that will be sent to Sentry.</param>
        /// <param name="sentryRequestFactory">The optional factory that will be used to create the <see cref="SentryRequest"/> that will be sent to Sentry.</param>
        /// <param name="sentryUserFactory">The optional factory that will be used to create the <see cref="SentryUser"/> that will be sent to Sentry.</param>
        /// <exception cref="System.ArgumentNullException">dsn</exception>
        public RavenClientNetCore(Dsn dsn,
                           IHttpContextAccessor contextAccessor,
                           IJsonPacketFactory jsonPacketFactory = null,
                           ISentryRequestFactory sentryRequestFactory = null,
                           ISentryUserFactory sentryUserFactory = null)
        {
            if (dsn == null)
                throw new ArgumentNullException("dsn");

            this.currentDsn = dsn;
            this.contextAccessor = contextAccessor;
            this.jsonPacketFactory = jsonPacketFactory ?? new JsonPacketFactory();
            this.sentryRequestFactory = sentryRequestFactory ?? new SentryRequestFactory(contextAccessor);
            this.sentryUserFactory = sentryUserFactory ?? new SentryUserFactory();

            Logger = "root";
            Timeout = TimeSpan.FromSeconds(5);
            this.defaultTags = new Dictionary<string, string>();
        }


        /// <summary>
        /// Gets or sets the <see cref="Action"/> to execute if an error occurs when executing
        /// <see cref="Capture"/>.
        /// </summary>
        /// <value>
        /// The <see cref="Action"/> to execute if an error occurs when executing <see cref="Capture"/>.
        /// </value>
        public Action<Exception> ErrorOnCapture { get; set; }

        /// <summary>
        /// Enable Gzip Compression?
        /// Defaults to <c>false</c>.
        /// </summary>
        //public bool Compression { get; set; }

        /// <summary>
        /// The Dsn currently being used to log exceptions.
        /// </summary>
        public Dsn CurrentDsn
        {
            get { return this.currentDsn; }
        }

        /// <summary>
        /// Interface for providing a 'log scrubber' that removes
        /// sensitive information from exceptions sent to sentry.
        /// </summary>
        public IScrubber LogScrubber { get; set; }

        /// <summary>
        /// The name of the logger. The default logger name is "root".
        /// </summary>
        public string Logger { get; set; }

        /// <summary>
        /// The version of the application.
        /// </summary>
        public string Release { get; set; }

        /// <summary>
        /// The environment (e.g. production)
        /// </summary>
        public string Environment { get; set; }

        /// <summary>
        /// Default tags sent on all events.
        /// </summary>
        public IDictionary<string, string> Tags
        {
            get { return this.defaultTags; }
        }

        /// <summary>
        /// Gets or sets the timeout value in milliseconds for the HTTP communication with Sentry.
        /// </summary>
        /// <value>
        /// The number of milliseconds to wait before the request times out. The default is 5,000 milliseconds (5 seconds).
        /// </value>
        public TimeSpan Timeout { get; set; }


        /// <summary>Captures the specified <paramref name="event"/>.</summary>
        /// <param name="event">The event to capture.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID" /> of the successfully captured <paramref name="event" />, or <c>null</c> if it fails.
        /// </returns>
        public async Task<string> Capture(SentryEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException("event");

            @event.Tags = MergeTags(@event.Tags);
            var packet = this.jsonPacketFactory.Create(CurrentDsn.ProjectID, @event);
            return await Send(packet);
        }


        /// <summary>
        /// Captures the event.
        /// </summary>
        /// <param name="e">The <see cref="Exception" /> to capture.</param>
        /// <returns></returns>
        [Obsolete("Use CaptureException() instead.", true)]
        public async Task<string> CaptureEvent(Exception e)
        {
            return await CaptureException(e);
        }


        /// <summary>
        /// Captures the event.
        /// </summary>
        /// <param name="e">The <see cref="Exception" /> to capture.</param>
        /// <param name="tags">The tags to annotate the captured exception with.</param>
        /// <returns></returns>
        [Obsolete("Use CaptureException() instead.", true)]
        public Task<string> CaptureEvent(Exception e, Dictionary<string, string> tags)
        {
            return CaptureException(e, tags : tags);
        }


        /// <summary>
        /// Captures the <see cref="Exception" />.
        /// </summary>
        /// <param name="exception">The <see cref="Exception" /> to capture.</param>
        /// <param name="message">The optional message to capture. Default: <see cref="Exception.Message" />.</param>
        /// <param name="level">The <see cref="ErrorLevel" /> of the captured <paramref name="exception" />. Default: <see cref="ErrorLevel.Error"/>.</param>
        /// <param name="tags">The tags to annotate the captured <paramref name="exception" /> with.</param>
        /// <param name="fingerprint">The custom fingerprint to annotate the captured <paramref name="message" /> with.</param>
        /// <param name="extra">The extra metadata to send with the captured <paramref name="exception" />.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID" /> of the successfully captured <paramref name="exception" />, or <c>null</c> if it fails.
        /// </returns>
        [Obsolete("Use Capture(SentryEvent) instead")]
        public async Task<string> CaptureException(Exception exception,
                                       SentryMessage message = null,
                                       ErrorLevel level = ErrorLevel.Error,
                                       IDictionary<string, string> tags = null,
                                       string[] fingerprint = null,
                                       object extra = null)
        {
            var @event = new SentryEvent(exception)
            {
                Message = message,
                Level = level,
                Extra = extra,
                Tags = MergeTags(tags),
                Fingerprint = fingerprint
            };

            return await Capture(@event);
        }


        /// <summary>
        /// Captures the message.
        /// </summary>
        /// <param name="message">The message to capture.</param>
        /// <param name="level">The <see cref="ErrorLevel" /> of the captured <paramref name="message"/>. Default <see cref="ErrorLevel.Info"/>.</param>
        /// <param name="tags">The tags to annotate the captured <paramref name="message"/> with.</param>
        /// <param name="fingerprint">The custom fingerprint to annotate the captured <paramref name="message" /> with.</param>
        /// <param name="extra">The extra metadata to send with the captured <paramref name="message"/>.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID"/> of the successfully captured <paramref name="message"/>, or <c>null</c> if it fails.
        /// </returns>
        [Obsolete("Use Capture(SentryEvent) instead")]
        public async Task<string> CaptureMessage(SentryMessage message,
                                     ErrorLevel level = ErrorLevel.Info,
                                     IDictionary<string, string> tags = null,
                                     string[] fingerprint = null,
                                     object extra = null)
        {
            var @event = new SentryEvent(message)
            {
                Level = level,
                Extra = extra,
                Tags = MergeTags(tags),
                Fingerprint = fingerprint
            };

            return await Capture(@event);
        }


        /// <summary>
        /// Performs <see cref="JsonPacket"/> post-processing prior to being sent to Sentry.
        /// </summary>
        /// <param name="packet">The prepared <see cref="JsonPacket"/> which has cleared the creation pipeline.</param>
        /// <returns>The <see cref="JsonPacket"/> which should be sent to Sentry.</returns>
        protected virtual JsonPacket PreparePacket(JsonPacket packet)
        {
            packet.Logger = String.IsNullOrWhiteSpace(packet.Logger)
                            || (packet.Logger == "root" && !String.IsNullOrWhiteSpace(Logger))
                ? Logger
                : packet.Logger;
            packet.User = packet.User ?? this.sentryUserFactory.Create();
            packet.Request = packet.Request ?? this.sentryRequestFactory.Create();
            packet.Release = String.IsNullOrWhiteSpace(packet.Release) ? Release : packet.Release;
            packet.Environment = String.IsNullOrWhiteSpace(packet.Environment) ? Environment : packet.Environment;
            return packet;
        }


        /// <summary>Sends the specified packet to Sentry.</summary>
        /// <param name="packet">The packet to send.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID" /> of the successfully captured JSON packet, or <c>null</c> if it fails.
        /// </returns>
        protected async virtual Task<string> Send(JsonPacket packet)
        {
            try
            {
                packet = PreparePacket(packet);


                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    client.BaseAddress = this.currentDsn.SentryUri;
                    client.DefaultRequestHeaders.Add("User-Agent", PacketBuilder.UserAgent);
                    client.DefaultRequestHeaders.Add("X-Sentry-Auth", PacketBuilder.CreateAuthenticationHeader(this.currentDsn));
                    HttpResponseMessage responseMessage = await client.PostAsync(
                        this.currentDsn.SentryUri,
                        new StringContent(
                            packet.ToString(Formatting.None),
                            Encoding.UTF8,
                            "application/json"
                        )
                    );

                    var responseData = await responseMessage.Content.ReadAsStringAsync();
                    return responseData;

                }

            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }


        private string HandleException(Exception exception)
        {
            try
            {
                if (ErrorOnCapture != null)
                {
                    ErrorOnCapture(exception);
                    return null;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[ERROR] ");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(exception);

                var webException = exception as WebException;
                if (webException == null || webException.Response == null)
                    return null;

                string messageBody;
                using (var stream = webException.Response.GetResponseStream())
                {
                    if (stream == null)
                        return null;

                    using (var sw = new StreamReader(stream))
                    {
                        messageBody = sw.ReadToEnd();
                    }
                }

                Console.WriteLine("[MESSAGE BODY] " + messageBody);
            }
            catch (Exception onErrorException)
            {
                Console.WriteLine(onErrorException);
            }

            return null;
        }


        private IDictionary<string, string> MergeTags(IDictionary<string, string> tags = null)
        {
            if (tags == null)
                return this.defaultTags;

            return this.defaultTags
                .Where(kv => !tags.Keys.Contains(kv.Key))
                .Concat(tags)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
    }
}