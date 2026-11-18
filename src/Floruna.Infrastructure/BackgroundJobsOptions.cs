namespace Floruna.Infrastructure;

/// <summary>Liga/desliga os jobs deste processo (papel api × worker).</summary>
public sealed class BackgroundJobsOptions
{
    public const string SectionName = "BackgroundJobs";

    public bool Enabled { get; set; } = true;
}
