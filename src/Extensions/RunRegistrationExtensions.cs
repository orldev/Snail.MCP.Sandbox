using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Snail.MCP.Sandbox.Adapters;
using Snail.MCP.Sandbox.Application;
using Snail.MCP.Sandbox.Application.Ports;
using Snail.MCP.Sandbox.Configuration;

namespace Snail.MCP.Sandbox.Extensions;

/// <summary>What a run is made of: the downloader of its inputs and the containers it runs in.</summary>
public static class RunRegistrationExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>The run itself and the two parts it is made of.</summary>
        public IServiceCollection AddRuns()
        {
            services
                .AddInputs()
                .AddDocker()
                .AddSingleton<Runs>();

            return services;
        }

        /// <summary>Signed-link downloads that follow no redirect, so an allowed host cannot hand a run to another one.</summary>
        public IServiceCollection AddInputs()
        {
            services
                .AddHttpClient(nameof(LinkInputs), client => client.Timeout = TimeSpan.FromMinutes(2))
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton<IInputs, LinkInputs>();

            return services;
        }

        /// <summary>The node's Docker daemon, the containers made on it and the sweep of what an earlier process left.</summary>
        /// <remarks>
        /// The client's own timeout is the longest run a caller may ask for plus the container's grace: an exec's
        /// output is one response, and a shorter client timeout would cut a legitimate run off before its own clock.
        /// </remarks>
        public IServiceCollection AddDocker()
        {
            services.AddSingleton<IDockerClient>(provider =>
            {
                var limits = provider.GetRequiredService<IOptions<SandboxOptions>>().Value;

                return new DockerClientBuilder()
                    .WithEndpoint(new Uri(limits.DockerEndpoint))
                    .WithTimeout(TimeSpan.FromSeconds(limits.MaxTimeoutSeconds) + Confinement.Grace)
                    .Build();
            });
            services.AddSingleton<IContainers, DockerContainers>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LeftoverContainers>());

            return services;
        }
    }
}
