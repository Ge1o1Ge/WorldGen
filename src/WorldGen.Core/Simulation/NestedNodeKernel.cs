namespace WorldGen.Core.Simulation;

public enum NodeActivityMode : byte
{
    Active,
    Sleeping,
    FastForward
}

public sealed record NodeInputSignal(string Id, double Value, double StableCorridor);

public sealed record NodeInputFrame(
    int Day,
    IReadOnlyList<NodeInputSignal> Signals,
    IReadOnlyList<string> EventIds);

public sealed record NodeEvaluation(
    IReadOnlyDictionary<string, double> Outbound,
    long StateVersion,
    int NextWakeDay,
    NodeActivityMode NextMode);

public sealed record NodeExecutionResult(NodeEvaluation Evaluation, bool UsedCachedResult);

public interface INestedSimulationNode
{
    string Id { get; }
    NodeEvaluation Evaluate(NodeInputFrame input, int elapsedDays);
}

/// <summary>
/// Treats a node as a stateful transducer. Stable input corridors may reuse outbound values,
/// but an event or scheduled wake always unfolds the node and runs its internal model.
/// </summary>
public sealed class NestedNodeKernel
{
    private readonly Dictionary<string, CachedNodeEvaluation> cache = new(StringComparer.Ordinal);

    public NodeExecutionResult Execute(INestedSimulationNode node, NodeInputFrame input)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(node.Id)) throw new ArgumentException("У ноды должен быть идентификатор", nameof(node));
        if (input.Day < 0) throw new ArgumentOutOfRangeException(nameof(input));
        if (input.Signals.Any(signal => string.IsNullOrWhiteSpace(signal.Id) || !double.IsFinite(signal.Value) ||
            !double.IsFinite(signal.StableCorridor) || signal.StableCorridor <= 0))
            throw new ArgumentException("Некорректный входной сигнал ноды", nameof(input));

        var signature = InputSignature.Create(input.Signals);
        if (cache.TryGetValue(node.Id, out var cached) &&
            input.EventIds.Count == 0 &&
            input.Day < cached.Evaluation.NextWakeDay &&
            signature == cached.Signature &&
            cached.Evaluation.NextMode != NodeActivityMode.Active)
        {
            return new NodeExecutionResult(cached.Evaluation, true);
        }

        var elapsedDays = cache.TryGetValue(node.Id, out cached)
            ? Math.Max(1, input.Day - cached.EvaluatedDay)
            : 1;
        var evaluation = node.Evaluate(input, elapsedDays);
        if (evaluation.NextWakeDay <= input.Day)
            throw new InvalidOperationException($"Нода {node.Id} назначила пробуждение не в будущем");
        cache[node.Id] = new CachedNodeEvaluation(input.Day, signature, evaluation);
        return new NodeExecutionResult(evaluation, false);
    }

    public void Invalidate(string nodeId) => cache.Remove(nodeId);

    private sealed record CachedNodeEvaluation(int EvaluatedDay, InputSignature Signature, NodeEvaluation Evaluation);

    private readonly record struct InputSignature(string Value)
    {
        public static InputSignature Create(IEnumerable<NodeInputSignal> signals)
        {
            var parts = signals.OrderBy(signal => signal.Id, StringComparer.Ordinal)
                .Select(signal => $"{signal.Id}:{Math.Floor(signal.Value / signal.StableCorridor)}");
            return new InputSignature(string.Join('|', parts));
        }
    }
}
