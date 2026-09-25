namespace DocQaMs.Web.Components.Ds;

/// <summary>A button's look, as the design system names it.</summary>
public enum ButtonVariant
{
    /// <summary>Solid green: the one main action in a view.</summary>
    Primary,

    /// <summary>Card-colored with a letterpress edge.</summary>
    Secondary,

    /// <summary>Transparent until hovered.</summary>
    Ghost,

    /// <summary>Royal purple, for a destructive action.</summary>
    Danger,

    /// <summary>Paper on an ink ground.</summary>
    Inverse,
}

/// <summary>A control's height: 30, 38 or 46 pixels.</summary>
public enum ControlSize
{
    /// <summary>30 pixels.</summary>
    Sm,

    /// <summary>38 pixels.</summary>
    Md,

    /// <summary>46 pixels.</summary>
    Lg,
}

/// <summary>A badge's color. Status is always in the badge's text too, never in its color alone.</summary>
public enum BadgeTone
{
    /// <summary>Grey.</summary>
    Neutral,

    /// <summary>Soft green.</summary>
    Accent,

    /// <summary>Green.</summary>
    Success,

    /// <summary>Gold.</summary>
    Warning,

    /// <summary>Purple.</summary>
    Danger,

    /// <summary>Ice blue.</summary>
    Info,

    /// <summary>Ink.</summary>
    Solid,
}

/// <summary>A card's look.</summary>
public enum CardVariant
{
    /// <summary>Ivory, with a hairline and a resting shadow.</summary>
    Default,

    /// <summary>No shadow.</summary>
    Plain,

    /// <summary>Sunk into the page.</summary>
    Sunken,

    /// <summary>An ink plate.</summary>
    Inverse,

    /// <summary>Square corners and a double rule.</summary>
    Framed,
}

/// <summary>Which side of its trigger a tooltip appears on.</summary>
public enum TooltipPlacement
{
    /// <summary>Above.</summary>
    Top,

    /// <summary>Below.</summary>
    Bottom,

    /// <summary>To the left.</summary>
    Left,

    /// <summary>To the right.</summary>
    Right,
}

/// <summary>The glyphs the app uses, copied from the design system's <c>ICON_PATHS</c> (Lucide geometry).</summary>
public enum IconName
{
    /// <summary>The Conversation screen.</summary>
    Feather,

    /// <summary>The Documents screen.</summary>
    BookOpen,

    /// <summary>The Corpus settings screen (the design system draws it as sliders).</summary>
    Settings,

    /// <summary>Stop.</summary>
    X,

    /// <summary>A select's caret.</summary>
    ChevronDown,
}

/// <summary>One option of a <see cref="Select"/>.</summary>
/// <param name="Value">The value the select reports when it is chosen.</param>
/// <param name="Label">What the option shows.</param>
/// <param name="DisabledReason">
/// Why the option cannot be chosen, shown beside its label; <see langword="null"/> for an option that can be.
/// A disabled option always says why.
/// </param>
public sealed record SelectOption(string Value, string Label, string? DisabledReason = null)
{
    /// <summary>Whether the option cannot be chosen.</summary>
    public bool IsDisabled => DisabledReason is not null;
}

/// <summary>Builds the design system's class lists and element ids.</summary>
internal static class Css
{
    /// <summary>Joins the class names that are not empty, with single spaces.</summary>
    public static string Join(params string?[] names) =>
        string.Join(' ', names.Where(name => !string.IsNullOrWhiteSpace(name)));

    /// <summary>The design system's modifier for an option: <c>Primary</c> becomes <c>primary</c>.</summary>
    public static string Modifier(Enum value) => value.ToString().ToLowerInvariant();

    /// <summary>An id unique within the page, for tying a label, hint or tooltip to its control.</summary>
    public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>
    /// The <c>aria-describedby</c> list: whatever the caller passed, then the component's own ids.
    /// <see langword="null"/> when there is nothing, so the attribute is left out.
    /// </summary>
    public static string? DescribedBy(IReadOnlyDictionary<string, object>? attributes, params string?[] ids)
    {
        string? passed = attributes is not null && attributes.TryGetValue("aria-describedby", out object? value)
            ? value?.ToString()
            : null;
        string joined = Join([passed, .. ids]);
        return joined.Length == 0 ? null : joined;
    }
}
