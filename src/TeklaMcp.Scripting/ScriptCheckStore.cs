using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaMcp.Scripting;

/// <summary>
/// The scripts <c>tekla_check_csharp</c> accepted, kept for a while so <c>tekla_run_csharp</c> can
/// run one by <c>checkId</c> instead of receiving the whole source a third time (issue #40: 2–20 KB
/// scripts went through the conversation for the check, the approval and the run). The approval
/// contract is unchanged: a mutating run still needs <c>expectedSha256</c>, compared with the
/// stored source. In memory only — a server restart forgets every check.
/// </summary>
public sealed class ScriptCheckStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(4);
    public const int Capacity = 100;

    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTime> _clock;

    private sealed class Entry
    {
        public string Code = "";
        public DateTime StoredAt;
    }

    public ScriptCheckStore(TimeSpan? lifetime = null, Func<DateTime>? clock = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Stores a checked script and returns its id ("chk-" + the first 16 hex of its hash).</summary>
    public string Add(string code)
    {
        var id = "chk-" + ScriptEngine.ComputeCodeSha256(code).Substring(0, 16);
        lock (_gate)
        {
            Prune();
            if (!_entries.ContainsKey(id) && _entries.Count >= Capacity)
                _entries.Remove(_entries.OrderBy(e => e.Value.StoredAt).First().Key);
            _entries[id] = new Entry { Code = code, StoredAt = _clock() };
        }
        return id;
    }

    /// <summary>The checked source for <paramref name="checkId"/>, or null when unknown or expired.</summary>
    public string? Get(string? checkId)
    {
        var id = (checkId ?? "").Trim();
        if (id.Length == 0) return null;
        lock (_gate)
        {
            Prune();
            return _entries.TryGetValue(id, out var entry) ? entry.Code : null;
        }
    }

    private void Prune()
    {
        var now = _clock();
        foreach (var id in _entries.Where(e => now - e.Value.StoredAt > _lifetime).Select(e => e.Key).ToList())
            _entries.Remove(id);
    }
}
