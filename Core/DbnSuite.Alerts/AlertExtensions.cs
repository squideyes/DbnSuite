using DbnSuite.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.Hosting;

public static class AlertExtensions
{
    /// <summary>
    /// Adds <see cref="AlertMail"/>, configured from the <c>Alerts</c> section. With no
    /// SMTP host set, alerts are off and nothing is sent.
    /// </summary>
    public static TBuilder AddAlertMail<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOptions<AlertOptions>()
            .Bind(builder.Configuration.GetSection(AlertOptions.Section))
            .Validate(AlertOptions.IsValid,
                "Alerts: with Host set, From and To must be email addresses, Port 1 to 65535 and Every positive.")
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAlertSender, SmtpAlertSender>();
        builder.Services.AddSingleton<AlertMail>();

        return builder;
    }
}
