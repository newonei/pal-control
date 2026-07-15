using Microsoft.Extensions.Options;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

/// <summary>
/// Makes the Scheme A content pointer and complete product projection ready
/// before an enabled economy accepts HTTP traffic. A fresh installation no
/// longer depends on the first player catalog request to create its bootstrap
/// version, and a missing or invalid local game catalog fails startup instead
/// of leaving an apparently enabled economy without published content.
/// </summary>
public sealed class EconomyContentStartupInitializer(
    EconomyContentRuntimeService runtime,
    IOptions<ExtractionModeOptions> options,
    ILogger<EconomyContentStartupInitializer> logger) : IHostedService
{
    private readonly ExtractionModeOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var content = await runtime.EnsureCurrentForBusinessDateAsync(cancellationToken);
            logger.LogInformation(
                "Economy content startup activation is ready at version {VersionNumber} for business date {BusinessDate}.",
                content.Version.VersionNumber,
                content.Version.BusinessDate);
        }
        catch (ContentValidationException exception)
        {
            logger.LogCritical(
                "Scheme A bootstrap content failed validation: {ValidationIssues}",
                string.Join("; ", exception.Validation.Errors.Select(issue =>
                    $"{issue.Code}@{issue.Path}: {issue.Message}")));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
