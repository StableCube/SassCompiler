using System;
using StableCube.SassCompiler;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjectionExtensions
{
    public static OptionsBuilder AddSassCompiler(
        this IServiceCollection services, 
        Action<SassCompilerOptions> optionsAction
    )
    {
        SassCompilerOptions ops = new ();
        optionsAction.Invoke(ops);

        return new OptionsBuilder(services, ops);
    }
}
