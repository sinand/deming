namespace Deming.Server.Panel;

/// <summary>
/// Marker for the panel's string resources (PanelText.resx = English, PanelText.tr.resx = Turkish).
/// Every user-facing panel string lives there; interpolation is done with resource parameters ({0}),
/// never by concatenating translated pieces.
/// </summary>
public sealed class PanelText;
