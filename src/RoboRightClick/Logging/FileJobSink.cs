using RoboRightClick.Core;

namespace RoboRightClick.Logging;

/// <summary>
/// Normal-mode sink for one job: jobs\&lt;folder&gt;\job.json (rewritten from a
/// <see cref="JobRecord"/> on each state change and at the end, via a temp file and
/// File.Replace, never on progress) and robocopy.log (UTF-8 copy of the pipe output,
/// written by the app, one header line per command, through a 64 KB buffered writer
/// flushed about once a second, on state changes and at the end). robocopy.log stops at
/// <see cref="MaxRobocopyLogBytes"/> with one "truncated" line. On <see cref="JobFinished"/>
/// it appends the history line; pruning is the manager's job. Called from the job thread
/// and the run's consumer thread; serialized by one lock. Write failures (disk full, folder
/// deleted) are swallowed after the first one is recorded in memory: logging must never
/// fail a copy.
/// </summary>
internal sealed class FileJobSink : IJobSink, IDisposable
{
    public const long MaxRobocopyLogBytes = 50L * 1024 * 1024;

    public FileJobSink(JobLogStore store, string jobFolder)
    {
        Store = store;
        JobFolder = jobFolder;
    }

    public JobLogStore Store { get; }

    public string JobFolder { get; }

    public void JobCreated(JobDescription job) => throw new NotImplementedException();

    public void StateChanged(Guid jobId, StateChange change) => throw new NotImplementedException();

    public void CommandStarted(Guid jobId, string arguments) => throw new NotImplementedException();

    public void OutputLine(Guid jobId, string line) => throw new NotImplementedException();

    public void CommandFinished(Guid jobId, int exitCode) => throw new NotImplementedException();

    public void JobFinished(JobSummary summary) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
