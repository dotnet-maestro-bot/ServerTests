// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ServerComparison.FunctionalTests
{
    public class LoggingHandler: DelegatingHandler
    {
        private readonly ILogger _logger;

        // Strongly consider limiting the number of retries - "retry forever" is
        // probably not the most user friendly way you could respond to "the
        // network cable got pulled out."
        private const int MaxRetries = 3;

        public LoggingHandler(HttpMessageHandler innerHandler, ILogger logger)
            : base(innerHandler)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug(request.ToString());
            var response = await base.SendAsync(request, cancellationToken);
            _logger.LogDebug(response.ToString());
            return response;
        }
    }

    public class RetryHandler : DelegatingHandler
    {
        private readonly ILogger _logger;

        // Strongly consider limiting the number of retries - "retry forever" is
        // probably not the most user friendly way you could respond to "the
        // network cable got pulled out."
        private const int MaxRetries = 3;

        public RetryHandler(HttpMessageHandler innerHandler, ILogger logger)
            : base(innerHandler)
        {
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            for (int i = 0; i < MaxRetries; i++)
            {
                response = await base.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) {
                    return response;
                }
                _logger.LogDebug($"Retrying {i+1}th time");
            }

            return response;
        }
    }

    public class FunctionalTestsBase : LoggedTest, IDisposable
    {
        private ApplicationDeployer _deployer;

        protected async Task<DeploymentResult> DeployAsync(DeploymentParameters parameters)
        {
            _deployer = ApplicationDeployerFactory.Create(parameters, LoggerFactory);

            var result = await _deployer.DeployAsync();
            // These two should be created in ApplicationDeployer
            RetryingHttpClient = new HttpClient(new RetryHandler(new LoggingHandler(new SocketsHttpHandler(), Logger), Logger));
            HttpClient = new HttpClient(new LoggingHandler(new SocketsHttpHandler(), Logger));
            return result;
        }

        public HttpClient RetryingHttpClient { get; private set; }

        public HttpClient HttpClient { get; private set; }

        public void Dispose()
        {
            _deployer?.Dispose();
        }
    }

    public class HelloWorldTests : FunctionalTestsBase
    {
        public static TestMatrix TestVariants
            => TestMatrix.ForServers(ServerType.IISExpress, ServerType.Kestrel, ServerType.Nginx, ServerType.HttpSys)
                .WithTfms(Tfm.NetCoreApp22, Tfm.Net461)
                .WithAllApplicationTypes()
                .WithAllAncmVersions()
                .WithAllHostingModels();

        [ConditionalTheory]
        [MemberData(nameof(TestVariants))]
        public async Task HelloWorld(TestVariant variant)
        {
            var deploymentParameters = new DeploymentParameters(variant)
            {
                ApplicationPath = Helpers.GetApplicationPath(variant.ApplicationType),
                EnvironmentName = "HelloWorld", // Will pick the Start class named 'StartupHelloWorld',
                ServerConfigTemplateContent = Helpers.GetConfigContent(variant.Server, "Http.config", "nginx.conf"),
                SiteName = "HttpTestSite", // This is configured in the Http.config
            };

            await DeployAsync(deploymentParameters);

            // Request to base address and check if various parts of the body are rendered & measure the cold startup time.
            var response = await RetryingHttpClient.GetAsync(string.Empty);
            var responseText = await response.Content.ReadAsStringAsync();
            Assert.Equal("Hello World", responseText);

            // Make sure it was the right server.
            var serverHeader = response.Headers.Server.ToString();
            switch (variant.Server)
            {
                case ServerType.HttpSys:
                    Assert.Equal("Microsoft-HTTPAPI/2.0", serverHeader);
                    break;
                case ServerType.Nginx:
                    Assert.StartsWith("nginx/", serverHeader);
                    break;
                case ServerType.Kestrel:
                    Assert.Equal("Kestrel", serverHeader);
                    break;
                case ServerType.IIS:
                case ServerType.IISExpress:
                    if (variant.HostingModel == HostingModel.OutOfProcess)
                    {
                        Assert.Equal("Kestrel", serverHeader);
                    }
                    else
                    {
                        Assert.StartsWith("Microsoft-IIS/", serverHeader);
                    }
                    break;
                default:
                    throw new NotImplementedException(variant.Server.ToString());
            }
        }
    }
}
