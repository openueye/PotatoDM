using PotatoDM.AppCore.Models;

namespace PotatoDM.AppCore.Job;

public enum JobFilter
{
    All,
    Downloading,
    Completed,
    Failed,
}

/// <summary>Pure matching rules for the search box and filter chips.</summary>
public static class JobFiltering
{
    public static bool Matches(DownloadJob job, string? searchText, JobFilter filter)
    {
        if (!MatchesState(job.State, filter))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var term = searchText.Trim();

        return job.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               job.Url.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesState(DownloadJobState state, JobFilter filter) =>
        filter switch
        {
            JobFilter.Downloading => state is
                DownloadJobState.Probing or DownloadJobState.Queued or DownloadJobState.Downloading or DownloadJobState.Paused,
            JobFilter.Completed => state == DownloadJobState.Completed,
            JobFilter.Failed => state == DownloadJobState.Failed,
            _ => true,
        };
}
