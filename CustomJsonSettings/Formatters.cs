using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RIoT2.Net.Orchestrator.CustomJsonSettings
{
    public class SpecificSystemTextJsonInputFormatter : SystemTextJsonInputFormatter
    {
        public SpecificSystemTextJsonInputFormatter(string settingsName, JsonOptions options, ILogger<SpecificSystemTextJsonInputFormatter> logger)
            : base(options, logger)
        {
            SettingsName = settingsName;
        }

        public string SettingsName { get; }

        public override bool CanRead(InputFormatterContext context)
        {
            if (context.HttpContext.Request.Headers["json-naming-policy"] != SettingsName)
                return false;

            return base.CanRead(context);
        }
    }

    public class SpecificSystemTextJsonOutputFormatter : SystemTextJsonOutputFormatter
    {
        public SpecificSystemTextJsonOutputFormatter(string settingsName, JsonSerializerOptions jsonSerializerOptions) : base(jsonSerializerOptions)
        {
            SettingsName = settingsName;
        }

        public string SettingsName { get; }

        public override bool CanWriteResult(OutputFormatterCanWriteContext context)
        {
            if (context.HttpContext.Request.Headers["json-naming-policy"] != SettingsName)
                return false;

            return base.CanWriteResult(context);
        }
    }

    public class ConfigureMvcJsonOptions : IConfigureOptions<MvcOptions>
    {
        private readonly string _jsonSettingsName;
        private readonly IOptionsMonitor<JsonOptions> _jsonOptions;
        private readonly ILoggerFactory _loggerFactory;

        public ConfigureMvcJsonOptions(
            string jsonSettingsName,
            IOptionsMonitor<JsonOptions> jsonOptions,
            ILoggerFactory loggerFactory)
        {
            _jsonSettingsName = jsonSettingsName;
            _jsonOptions = jsonOptions;
            _loggerFactory = loggerFactory;
        }

        public void Configure(MvcOptions options)
        {
            var jsonOptions = _jsonOptions.Get(_jsonSettingsName);
            var logger = _loggerFactory.CreateLogger<SpecificSystemTextJsonInputFormatter>();
            options.InputFormatters.Insert(
                0,
                new SpecificSystemTextJsonInputFormatter(
                    _jsonSettingsName,
                    jsonOptions,
                    logger));
            options.OutputFormatters.Insert(
                0,
                new SpecificSystemTextJsonOutputFormatter(
                    _jsonSettingsName,
                    jsonOptions.JsonSerializerOptions));
        }
    }
}
