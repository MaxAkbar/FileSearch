using FileSearch.Core.Extractors;
using Microsoft.Extensions.DependencyInjection;

namespace FileSearch.WindowsOcr;

public static class WindowsOcrServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsImageOcr(
        this IServiceCollection services,
        Func<IServiceProvider, bool>? isEnabled = null,
        Action<IServiceProvider, WindowsImageOcrOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var options = new WindowsImageOcrOptions
            {
                IsEnabled = () => isEnabled?.Invoke(sp) ?? true,
            };
            configure?.Invoke(sp, options);
            return options;
        });

        services.AddSingleton<IEmbeddedImageOcrService>(sp => new WindowsEmbeddedImageOcrService(
            sp.GetRequiredService<WindowsImageOcrOptions>()));

        services.AddSingleton<ITextExtractor>(sp => new WindowsImageOcrExtractor(
            sp.GetRequiredService<WindowsImageOcrOptions>()));

        services.AddSingleton<ITextExtractor>(sp => new WindowsPdfOcrExtractor(
            sp.GetRequiredService<WindowsImageOcrOptions>(),
            new PdfExtractor()));

        return services;
    }
}
