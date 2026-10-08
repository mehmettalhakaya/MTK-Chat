using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MTKChat.Server.Configuration;

namespace MTKChat.Server.Agents;

public sealed class AgentRateLimiter(IOptions<AgentOptions> options)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _requests = new();

    public bool TryAcquire(Guid userId)
    {
        var queue = _requests.GetOrAdd(userId, static _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            while (queue.TryPeek(out var timestamp) && timestamp < cutoff) queue.Dequeue();
            if (queue.Count >= Math.Max(1, options.Value.MaxRequestsPerUserPerHour)) return false;
            queue.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }
}
