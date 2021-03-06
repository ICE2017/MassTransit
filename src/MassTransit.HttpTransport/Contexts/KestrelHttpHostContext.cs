// Copyright 2007-2018 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.HttpTransport.Contexts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Configuration;
    using GreenPipes;
    using GreenPipes.Payloads;
    using Hosting;
    using Logging;
    using Microsoft.AspNetCore;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Transport;


    public class KestrelHttpHostContext :
        BasePipeContext,
        HttpHostContext,
        IAsyncDisposable
    {
        static readonly ILog _log = Logger.Get<KestrelHttpHostContext>();

        readonly IHttpHostConfiguration _configuration;
        readonly SortedDictionary<string, List<Endpoint>> _endpoints;
        IWebHost _webHost;
        bool _started;

        public KestrelHttpHostContext(IHttpHostConfiguration configuration, CancellationToken cancellationToken)
            : base(new PayloadCache(), cancellationToken)
        {
            _configuration = configuration;

            _endpoints = new SortedDictionary<string, List<Endpoint>>(StringComparer.OrdinalIgnoreCase);
        }

        public HttpHostSettings HostSettings => _configuration.Settings;

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_log.IsDebugEnabled)
                _log.DebugFormat("Configuring Web Host: {0}", _configuration.HostAddress);

            IPHostEntry entries = await Dns.GetHostEntryAsync(_configuration.Settings.Host).ConfigureAwait(false);

            IWebHost BuildWebHost(params string[] args)
            {
                return WebHost.CreateDefaultBuilder(args)
                    .Configure(Configure)
                    .UseKestrel(options =>
                    {
                        foreach (var ipAddress in entries.AddressList)
                        {
                            options.Listen(ipAddress, HostSettings.Port);
                        }

                        //                        options.Listen(IPAddress.Loopback, 5001, listenOptions =>
                        //                        {
                        //                            listenOptions.UseHttps("testCert.pfx", "testPassword");
                        //                        });
                    })
                    .Build();
            }

            if (_log.IsDebugEnabled)
                _log.DebugFormat("Building Web Host: {0}", _configuration.HostAddress);

            _webHost = BuildWebHost();

            if (_log.IsDebugEnabled)
                _log.DebugFormat("Starting Web Host: {0}", _configuration.HostAddress);

            try
            {
                await _webHost.StartAsync(cancellationToken).ConfigureAwait(false);

                if (_log.IsDebugEnabled)
                    _log.DebugFormat("Started Web Host: {0}", _configuration.HostAddress);
            }
            catch (Exception exception)
            {
                if (_log.IsErrorEnabled)
                    _log.Error($"Fault Starting Web Host: {_configuration.HostAddress}", exception);

                throw;
            }

            _started = true;
        }

        public void RegisterEndpointHandler(string pathMatch, HttpConsumer handler)
        {
            if (_started)
                throw new InvalidOperationException("The host has already been started, no additional endpoints may be added.");

            if (_log.IsDebugEnabled)
                _log.DebugFormat("Adding Endpoint Handler: {0}", pathMatch);

            lock (_endpoints)
            {
                if (_endpoints.TryGetValue(pathMatch, out List<Endpoint> handlers))
                    handlers.Add(new Endpoint(pathMatch, handler));
                else
                    _endpoints.Add(pathMatch, new List<Endpoint> {new Endpoint(pathMatch, handler)});
            }
        }

        public async Task DisposeAsync(CancellationToken cancellationToken)
        {
            if (_started)
                await _webHost.StopAsync(cancellationToken).ConfigureAwait(false);

            _webHost?.Dispose();
        }

        void Configure(IApplicationBuilder app)
        {
            app.UseDeveloperExceptionPage();

            lock (_endpoints)
            {
                foreach (KeyValuePair<string, List<Endpoint>> endpoint in _endpoints.Reverse())
                {
                    string key = string.IsNullOrWhiteSpace(endpoint.Key)
                        ? string.Empty
                        : endpoint.Key[0] == '/'
                            ? endpoint.Key
                            : "/" + endpoint.Key;

                    Console.WriteLine("Mapping {0}", key);

                    foreach (var handler in endpoint.Value)
                        app.Map(key, x => x.Use(handler.Handler.Handle));
                }
            }
        }


        struct Endpoint
        {
            public readonly string PathMatch;
            public readonly HttpConsumer Handler;

            public Endpoint(string pathMatch, HttpConsumer handler)
            {
                PathMatch = pathMatch;
                Handler = handler;
            }
        }
    }
}