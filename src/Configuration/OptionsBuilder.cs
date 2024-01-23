using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace StableCube.SassCompiler;

public class OptionsBuilder
{
    private readonly IServiceCollection _services;
    private readonly SassCompilerOptions _options;
    
    public OptionsBuilder(IServiceCollection services, SassCompilerOptions options)
    {
        _services = services;
        _options = options;

        _services.AddSingleton(_options);

        _services.AddHostedService<FileChangeWatcher>();
    }
}
