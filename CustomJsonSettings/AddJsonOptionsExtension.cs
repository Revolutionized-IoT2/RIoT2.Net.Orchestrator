using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    public static class AddJsonOptionsExtension
    {
        public static IMvcBuilder AddJsonOptions(
        this IMvcBuilder builder,
        string settingsName,
        Action<JsonOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(settingsName, configure);
            builder.Services.AddSingleton<IConfigureOptions<MvcOptions>>(sp =>
            {
                var options = sp.GetRequiredService<IOptionsMonitor<JsonOptions>>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return new ConfigureMvcJsonOptions(settingsName, options, loggerFactory);
            });
            return builder;
        }
    }
}