namespace SmartX.Core.Insights;

/// <summary>A learned habit: when <see cref="Antecedent"/> happens, <see cref="Consequent"/> tends to follow.</summary>
/// <param name="Antecedent">The earlier event.</param>
/// <param name="Consequent">The event that followed it.</param>
/// <param name="Support">How many times the consequent followed the antecedent inside the window.</param>
/// <param name="AntecedentCount">How many times the antecedent happened at all.</param>
/// <param name="Confidence">Support divided by antecedent count - the chance the consequent follows.</param>
/// <param name="Lift">
/// Confidence divided by how common the consequent is in general. Above 1, the
/// antecedent genuinely makes the consequent more likely; near 1, the consequent
/// is just something the operator does all the time.
/// </param>
/// <param name="MeanGap">Average time between the two.</param>
public sealed record PatternRule(
    string Antecedent,
    string Consequent,
    int Support,
    int AntecedentCount,
    double Confidence,
    double Lift,
    TimeSpan MeanGap);

/// <summary>
/// Learns which operator actions tend to follow which events, from the stream of
/// alerts, searches and commands as they happen.
/// <para>
/// <b>The algorithm.</b> This is windowed sequential association mining - a
/// first-order model of operator behaviour. Every event is a token. When a token
/// <c>B</c> arrives, every distinct token <c>A</c> seen in the preceding window
/// (ten minutes by default) is credited with "was followed by B". From those
/// counts come the two standard measures of an association rule:
/// </para>
/// <code>
/// confidence(A → B) = times A was followed by B / times A happened
/// lift(A → B)       = confidence(A → B) / (times B happened / all events)
/// </code>
/// <para>
/// So when Soil Moisture Probe 01 has dropped below 20% five times, and four of
/// those were followed within a few minutes by a search for "pump a" and then by
/// switching Water Pump A on, the miner holds <c>alert → search</c> and
/// <c>alert → command</c> at 80% confidence. The next time that alert fires, the
/// engine can offer the command before the operator goes looking for it.
/// </para>
/// <para>
/// <b>The data structures.</b> The window is a <see cref="Queue{T}"/>, because
/// events leave it in the order they entered: each arrival evicts from the front
/// whatever has aged out, in amortised O(1). The counts live in a
/// <see cref="Dictionary{TKey,TValue}"/> of dictionaries - an adjacency map of a
/// weighted directed graph whose vertices are events and whose edge weights are
/// "followed by" counts - so recording one pair and listing a token's successors
/// are both O(1) to reach. Each event in the window keeps a
/// <see cref="HashSet{T}"/> of the successors it has already been credited with, so
/// one alert followed by three identical searches counts once. That keeps
/// confidence a true proportion that can never exceed 1.
/// </para>
/// <para>
/// Learning is incremental: each event costs O(w) for a window holding w events,
/// and nothing is ever recomputed from scratch. Events are expected in time order.
/// Not thread-safe: the stream store serialises access.
/// </para>
/// </summary>
public sealed class SequentialPatternMiner
{
    private readonly Queue<WindowEntry> _window = new();
    private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, FollowTally>> _followers = new(StringComparer.Ordinal);

    /// <summary>Creates a miner.</summary>
    /// <param name="window">How soon after an event a later one must come to count as following it.</param>
    public SequentialPatternMiner(TimeSpan? window = null)
    {
        Window = window ?? TimeSpan.FromMinutes(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero);
    }

    /// <summary>How soon an event must follow another to be linked to it.</summary>
    public TimeSpan Window { get; }

    /// <summary>Events observed.</summary>
    public long TotalEvents { get; private set; }

    /// <summary>Distinct tokens seen.</summary>
    public int DistinctTokens => _occurrences.Count;

    /// <summary>Distinct "A followed by B" pairs recorded - the edges of the behaviour graph.</summary>
    public int PairCount { get; private set; }

    /// <summary>Events currently inside the window.</summary>
    public int WindowSize => _window.Count;

    /// <summary>
    /// The events still inside the window, oldest first - the context the advisor
    /// treats as "what just happened".
    /// </summary>
    public IEnumerable<(string Token, DateTimeOffset TimestampUtc)> Recent
        => _window.Select(entry => (entry.Token, entry.TimestampUtc));

    /// <summary>Records an event and credits everything in the window before it.</summary>
    /// <param name="token">The event, from <see cref="ActivityToken"/>.</param>
    /// <param name="timestampUtc">When it happened.</param>
    public void Observe(string token, DateTimeOffset timestampUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Age out the front of the window first, so only events close enough to
        // this one are linked to it.
        while (_window.Count > 0 && timestampUtc - _window.Peek().TimestampUtc > Window)
        {
            _window.Dequeue();
        }

        foreach (var earlier in _window)
        {
            // An event is not its own successor, and each earlier event is credited
            // with a given successor at most once.
            if (earlier.Token == token || !earlier.Credited.Add(token))
            {
                continue;
            }

            if (!_followers.TryGetValue(earlier.Token, out var successors))
            {
                successors = new Dictionary<string, FollowTally>(StringComparer.Ordinal);
                _followers[earlier.Token] = successors;
            }

            if (!successors.TryGetValue(token, out var tally))
            {
                tally = new FollowTally();
                successors[token] = tally;
                PairCount++;
            }

            tally.Count++;
            tally.TotalGapSeconds += Math.Max(0d, (timestampUtc - earlier.TimestampUtc).TotalSeconds);
        }

        _occurrences[token] = _occurrences.GetValueOrDefault(token) + 1;
        _window.Enqueue(new WindowEntry(token, timestampUtc));
        TotalEvents++;
    }

    /// <summary>How many times a token has been seen.</summary>
    public int Occurrences(string token) => _occurrences.GetValueOrDefault(token);

    /// <summary>
    /// Everything that tends to follow <paramref name="antecedent"/>, most
    /// confident first.
    /// </summary>
    /// <param name="antecedent">The earlier event.</param>
    /// <param name="minSupport">Fewest times a pair must have been seen to be trusted.</param>
    /// <param name="minConfidence">Lowest confidence worth reporting.</param>
    public List<PatternRule> RulesFrom(string antecedent, int minSupport = 2, double minConfidence = 0.3d)
    {
        var rules = new List<PatternRule>();

        if (!_followers.TryGetValue(antecedent, out var successors))
        {
            return rules;
        }

        foreach (var (consequent, tally) in successors)
        {
            if (BuildRule(antecedent, consequent, tally) is { } rule
                && rule.Support >= minSupport
                && rule.Confidence >= minConfidence)
            {
                rules.Add(rule);
            }
        }

        rules.Sort(ByStrength);
        return rules;
    }

    /// <summary>Every rule that clears the thresholds, strongest first - what the engine has learned.</summary>
    public List<PatternRule> AllRules(int minSupport = 2, double minConfidence = 0.3d)
    {
        var rules = new List<PatternRule>();

        foreach (var (antecedent, successors) in _followers)
        {
            foreach (var (consequent, tally) in successors)
            {
                if (BuildRule(antecedent, consequent, tally) is { } rule
                    && rule.Support >= minSupport
                    && rule.Confidence >= minConfidence)
                {
                    rules.Add(rule);
                }
            }
        }

        rules.Sort(ByStrength);
        return rules;
    }

    private PatternRule? BuildRule(string antecedent, string consequent, FollowTally tally)
    {
        var antecedentCount = _occurrences.GetValueOrDefault(antecedent);
        var consequentCount = _occurrences.GetValueOrDefault(consequent);

        if (antecedentCount == 0 || consequentCount == 0 || TotalEvents == 0)
        {
            return null;
        }

        var confidence = Math.Min(1d, (double)tally.Count / antecedentCount);
        var baseRate = (double)consequentCount / TotalEvents;

        return new PatternRule(
            antecedent,
            consequent,
            tally.Count,
            antecedentCount,
            confidence,
            baseRate <= 0d ? 0d : confidence / baseRate,
            TimeSpan.FromSeconds(tally.TotalGapSeconds / tally.Count));
    }

    private static int ByStrength(PatternRule left, PatternRule right)
    {
        var byConfidence = right.Confidence.CompareTo(left.Confidence);
        return byConfidence != 0 ? byConfidence : right.Support.CompareTo(left.Support);
    }

    private sealed class FollowTally
    {
        public int Count;
        public double TotalGapSeconds;
    }

    private sealed class WindowEntry(string token, DateTimeOffset timestampUtc)
    {
        public string Token { get; } = token;

        public DateTimeOffset TimestampUtc { get; } = timestampUtc;

        public HashSet<string> Credited { get; } = new(StringComparer.Ordinal);
    }
}
