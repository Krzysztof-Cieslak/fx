using System;
using System.Threading.Tasks;

namespace Billing.Retry;

/// <summary>How to retry.</summary>
public sealed record RetryConfig(int Attempts, TimeSpan Delay);

public enum Outcome
{
    Succeeded,
    GaveUp,
}

public interface IClock
{
    DateTimeOffset Now { get; }
    Task Wait(TimeSpan delay);
}

[Serializable]
public class Retrier : IDisposable
{
    private readonly IClock clock;
    public const int MaxAttempts = 10;

    public Retrier(IClock clock)
    {
        this.clock = clock;
    }

    public int Count { get; private set; }

    public event EventHandler? Retried;

    public async Task<Outcome> ExecuteAsync(RetryConfig cfg, Func<Task> op)
    {
        for (var i = 0; i < cfg.Attempts; i++)
        {
            Count++;
        }
        return Outcome.GaveUp;
    }

    public void Dispose() { }

    private struct State
    {
        public int Attempt;
    }
}

public delegate bool Decide(int attempt);
