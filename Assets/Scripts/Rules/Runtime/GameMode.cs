namespace Game.Rules;

/// <summary>
/// Which ruleset the round runs. Selected from the main menu, stored on the shared runtime
/// <see cref="TagRulesConfig"/>, and read fresh by everything that branches on it — so switching
/// modes only needs a StartRound, never a rebuild.
/// </summary>
public enum GameMode
{
    /// <summary>Pest control vs raccoon: taggers hurl a net (<see cref="NetThrower"/>), a catch
    /// CONVERTS the victim to Tagger and the tagger stays one (an infection cascade), runners have
    /// the trash objective, and the local player being caught ends the round.</summary>
    PestControl,

    /// <summary>Classic tag: no nets, a short-range touch tag SWAPS the two roles (victim becomes IT,
    /// tagger becomes a Runner), everyone wears the pest_control model, there is no trash objective,
    /// and whoever is IT when the timer expires loses.</summary>
    Tag,
}
