using WorldGen.Core.Topology;

namespace WorldGen.Core.Knowledge;

public sealed record LocalTechnologyState
{
    public float Knowledge { get; set; }
    public float Competence { get; set; }
    public float Capability { get; set; }
    public float Adoption { get; set; }

    public LocalTechnologyState Copy() => new()
    {
        Knowledge = Knowledge,
        Competence = Competence,
        Capability = Capability,
        Adoption = Adoption
    };
}

public sealed record LocalTechnologyDiffusionSettings(
    float DiffusionRate,
    float UnpracticedEmission,
    float MinimumRecordedKnowledge)
{
    public static LocalTechnologyDiffusionSettings Default { get; } = new(0.12f, 0.025f, 0.0001f);
}

/// <summary>
/// Sparse zone-level technology layer. Mere awareness emits a weak signal; implemented
/// practice emits nearly the full known signal, preventing an automatic knowledge avalanche.
/// </summary>
public sealed class LocalTechnologyField
{
    private readonly IWorldTopology topology;
    private readonly Dictionary<TechnologyCellKey, LocalTechnologyState> states = new();

    public LocalTechnologyField(IWorldTopology topology) =>
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));

    public int StateCount => states.Count;

    public LocalTechnologyState GetState(string technologyId, CellAddress cell)
    {
        Validate(technologyId, cell);
        return states.TryGetValue(new TechnologyCellKey(technologyId, cell), out var state)
            ? state.Copy()
            : new LocalTechnologyState();
    }

    public void SetState(string technologyId, CellAddress cell, LocalTechnologyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(technologyId, cell);
        ValidateUnit(state.Knowledge, nameof(state.Knowledge));
        ValidateUnit(state.Competence, nameof(state.Competence));
        ValidateUnit(state.Capability, nameof(state.Capability));
        ValidateUnit(state.Adoption, nameof(state.Adoption));
        if (state.Competence > state.Knowledge || state.Adoption > Math.Min(state.Knowledge, Math.Min(state.Competence, state.Capability)))
            throw new ArgumentException("Практика не может опережать знание, компетенцию и возможность", nameof(state));
        states[new TechnologyCellKey(technologyId, cell)] = state.Copy();
    }

    public void DiffuseKnowledge(string technologyId, float elapsedSteps = 1, LocalTechnologyDiffusionSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(technologyId)) throw new ArgumentException("Технология не указана", nameof(technologyId));
        if (!float.IsFinite(elapsedSteps) || elapsedSteps <= 0) throw new ArgumentOutOfRangeException(nameof(elapsedSteps));
        settings ??= LocalTechnologyDiffusionSettings.Default;
        if (settings.DiffusionRate is <= 0 or > 1 || settings.UnpracticedEmission is < 0 or > 1 || settings.MinimumRecordedKnowledge <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings));

        var relevant = states.Where(pair => string.Equals(pair.Key.TechnologyId, technologyId, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key.Cell.Face).ThenBy(pair => pair.Key.Cell.Y).ThenBy(pair => pair.Key.Cell.X)
            .ToArray();
        var gains = new Dictionary<CellAddress, float>();
        foreach (var (key, source) in relevant)
        {
            var practiceEmission = settings.UnpracticedEmission + (1 - settings.UnpracticedEmission) * source.Adoption;
            var emittedKnowledge = source.Knowledge * practiceEmission;
            if (emittedKnowledge <= settings.MinimumRecordedKnowledge) continue;
            foreach (var neighbor in topology.GetNeighbors(key.Cell))
            {
                var receiverKnowledge = states.GetValueOrDefault(new TechnologyCellKey(technologyId, neighbor))?.Knowledge ?? 0;
                var gradient = emittedKnowledge - receiverKnowledge;
                if (gradient <= 0) continue;
                var gain = gradient * settings.DiffusionRate * elapsedSteps;
                gains[neighbor] = Math.Max(gains.GetValueOrDefault(neighbor), gain);
            }
        }

        foreach (var (cell, gain) in gains)
        {
            if (gain < settings.MinimumRecordedKnowledge) continue;
            var key = new TechnologyCellKey(technologyId, cell);
            if (!states.TryGetValue(key, out var state))
            {
                state = new LocalTechnologyState();
                states.Add(key, state);
            }
            state.Knowledge = Math.Clamp(state.Knowledge + gain * (1 - state.Knowledge), 0, 1);
        }
    }

    public void AdvancePractice(
        string technologyId,
        CellAddress cell,
        float learningOpportunity,
        float materialCapability,
        float localDemand)
    {
        Validate(technologyId, cell);
        ValidateUnit(learningOpportunity, nameof(learningOpportunity));
        ValidateUnit(materialCapability, nameof(materialCapability));
        ValidateUnit(localDemand, nameof(localDemand));
        var key = new TechnologyCellKey(technologyId, cell);
        if (!states.TryGetValue(key, out var state)) return;
        state.Competence += (Math.Min(state.Knowledge, learningOpportunity) - state.Competence) * 0.08f;
        state.Capability += (Math.Min(state.Competence, materialCapability) - state.Capability) * 0.07f;
        var adoptionTarget = Math.Min(localDemand, Math.Min(state.Knowledge, Math.Min(state.Competence, state.Capability)));
        state.Adoption += (adoptionTarget - state.Adoption) * 0.06f;
    }

    private void Validate(string technologyId, CellAddress cell)
    {
        if (string.IsNullOrWhiteSpace(technologyId)) throw new ArgumentException("Технология не указана", nameof(technologyId));
        if (!topology.Contains(cell)) throw new ArgumentOutOfRangeException(nameof(cell));
    }

    private static void ValidateUnit(float value, string name)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(name);
    }

    private readonly record struct TechnologyCellKey(string TechnologyId, CellAddress Cell);
}
