﻿// Unless explicitly stated otherwise all files in this repository are licensed
// under the Apache License Version 2.0.
// This product includes software developed at Datadog (https://www.datadoghq.com/).
// Copyright 2019 Datadog, Inc.

using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Text;
using Serilog.Debugging;
using System.Collections.Generic;
using Serilog.Events;

namespace Serilog.Sinks.Datadog.Logs
{
    /// <summary>
    /// TCP Client that forwards log events to Datadog.
    /// </summary>
    public class DatadogTcpClient : IDatadogClient
    {
        private readonly DatadogConfiguration _config;
        private readonly LogFormatter _formatter;
        private readonly string _apiKey;
        private TcpClient _client;
        private Stream _stream;

        /// <summary>
        /// API Key / message-content delimiter.
        /// </summary>
        private const string WhiteSpace = " ";

        /// <summary>
        /// Message delimiter.
        /// </summary>
        private const string MessageDelimiter = "\n";

        /// <summary>
        /// Max number of retries when sending failed.
        /// </summary>
        private const int MaxRetries = 5;

        /// <summary>
        /// Max backoff used when sending failed.
        /// </summary>
        private const int MaxBackoff = 30;

        /// <summary>
        /// Shared UTF8 encoder.
        /// </summary>
        private static readonly UTF8Encoding UTF8 = new UTF8Encoding();

        public DatadogTcpClient(DatadogConfiguration config, LogFormatter formatter, string apiKey)
        {
            _config = config;
            _formatter = formatter;
            _apiKey = apiKey;
        }

        /// <summary>
        /// Initialize a connection to Datadog logs-backend.
        /// </summary>
        private async Task ConnectAsync()
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_config.Url, _config.Port);
            var rawStream = _client.GetStream();
            if (_config.UseSSL)
            {
                var secureStream = new SslStream(rawStream);
                await secureStream.AuthenticateAsClientAsync(_config.Url);
                _stream = secureStream;
            }
            else
            {
                _stream = rawStream;
            }
        }

        private int _retry;

        public async Task WriteAsync(IEnumerable<LogEvent> events)
        {
            var payloadBuilder = new StringBuilder();
            foreach (var logEvent in events)
            {
                payloadBuilder.Append(_apiKey + WhiteSpace);
                payloadBuilder.Append(_formatter.formatMessage(logEvent));
                payloadBuilder.Append(MessageDelimiter);
            }
            var payload = payloadBuilder.ToString();

            if (_retry > 0) {
                var backoff = (int)Math.Min(Math.Pow(2, _retry), MaxBackoff);
                await Task.Delay(backoff * 1000);
            }

            _retry++;

            if (IsConnectionClosed())
            {
                await ConnectAsync();
            }

            try {
                var data = UTF8.GetBytes(payload);
                _stream.Write(data, 0, data.Length);
                _retry = 0;
            }
            catch (Exception) {
                CloseConnection();
                throw;
            }

            SelfLog.WriteLine("Could not send payload to Datadog after {_retry} retries. Payload: {0}", payload);
        }

        private void CloseConnection()
        {
#if NETSTANDARD1_3
            _client.Dispose();
            _stream.Dispose();
#else
            _client.Close();
            _stream.Close();
#endif
            _stream = null;
            _client = null;
        }

        private bool IsConnectionClosed()
        {
            return _client == null || _stream == null;
        }

        /// <summary>
        /// Close the client.
        /// </summary>
        public void Close()
        {
            if (!IsConnectionClosed())
            {
                try
                {
                    _stream.Flush();
                }
                catch (Exception e)
                {
                    SelfLog.WriteLine("Could not flush the remaining data: {0}", e);
                }
                CloseConnection();
            }
        }
    }
}
