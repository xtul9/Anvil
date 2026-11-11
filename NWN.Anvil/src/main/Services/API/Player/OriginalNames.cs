namespace Anvil.Services
{
  /// <summary>
  /// A player's names from before their first override, held as plain strings. A native wrapper would point at the
  /// live field, so the first override written there would also become the "original" that every restore writes back.
  /// </summary>
  internal sealed class OriginalNames
  {
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string PlayerName { get; init; } = "";
  }
}
